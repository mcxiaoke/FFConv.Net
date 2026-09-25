using System.Globalization;
using MediaCli.Transcode.Model;
using MediaCli.Transcode.Presets;
using MediaCli.Transcode.Run;
using MediaCli.Transcode.Support;

namespace MediaCli.Transcode.Gui;

/// <summary>
/// UI 选项 → core 入参的纯映射层。
///
/// 设计约束：本类型不引用任何 WinForms 类型，因此可直接被 xUnit 测试覆盖。
/// 语义与 CLI <c>Program.cs</c> 的 BuildArgv / BuildShim 逐条同构，
/// 保证「同一组输入 → 同一套 core 调用 → 同一条 ffmpeg 命令」。
///
/// <b>优先级</b>：CLI 参数框 &gt; 界面控件。界面控件是便捷入口，
/// 参数框是显式表达；冲突时以显式输入为准，并在日志中说明覆盖了哪个控件
/// （<see cref="CollectOverrideNotes"/>），绝不静默。
/// </summary>
public sealed class GuiOptions
{
    /// <summary>输入文件或目录（可多个）。</summary>
    public List<string> Inputs { get; init; } = [];

    /// <summary>预设名。别名（anime / av1 / h264 / hevc_anime …）由 core 解析，此处不重复实现。</summary>
    public string Preset { get; set; } = "av1_2k";

    /// <summary>输出目录；空字符串表示输出到源文件同目录。</summary>
    public string Output { get; set; } = "";

    /// <summary>tree | dir | file。</summary>
    public string OutputMode { get; set; } = "dir";

    /// <summary>auto | cuda | qsv | amf | d3d | d3d11va | d3d12va | dxva2 | cpu。</summary>
    public string Hwaccel { get; set; } = "auto";

    /// <summary>auto | gpu | cpu。</summary>
    public string DecodeMode { get; set; } = "auto";

    /// <summary>
    /// 自定义参数（FFConv CLI 风格，可含 <c>--ffargs</c>）。
    /// 例：<c>--video-bitrate 3M --video-quality 23 --include foo</c>
    /// </summary>
    public string CliArgs { get; set; } = "";

    public bool Override { get; set; }
    public bool Strict { get; set; }
    public bool Debug { get; set; }
    public bool Anime { get; set; }

    /// <summary>把本次日志同步一份到输出目录。</summary>
    public bool SyncLogToOutput { get; set; }

    // ---- 扫描过滤（由 CLI 参数驱动） ----
    public string? Include { get; private set; }
    public string? Exclude { get; private set; }
    public string? Regex { get; private set; }
    public int Start { get; private set; }
    public int Count { get; private set; } = int.MaxValue;

    private CliArgsParseResult? parsed;

    /// <summary>解析后的 CLI 参数（惰性、结果缓存）。</summary>
    public CliArgsParseResult ParsedArgs => parsed ??= CliArgParser.Parse(CliArgs);

    /// <summary>
    /// 空值归一为 <c>null</c>（表示「未指定」）；其余值<b>原样透传，包括 "auto"</b>。
    ///
    /// 刻意不做 auto→null 的改写。经实测（见 GuiMappingTests）：
    /// - <b>decodeMode=auto</b>：core 的 <c>CandidateTiers</c> 在 auto 分支<b>先于</b>
    ///   <c>normalizeHwaccelName</c> 的 d3d 别名拦截了 "auto"，因此传 "auto" 与传 null
    ///   产生的候选链<b>完全相同</b>（实测均为厂商层 → d3d → cpu）；
    /// - <b>decodeMode=gpu</b>：传 "auto" 表示「用默认硬件层」（JS 的既有语义），传 null 会
    ///   抛「requires --hwaccel」。透传才能与 JS/CLI 保持一致。
    /// </summary>
    public string? EffectiveHwaccel =>
        string.IsNullOrWhiteSpace(Hwaccel) ? null : Hwaccel.Trim();

    private string EffectiveOutputMode =>
        CliText("output-mode") ?? (string.IsNullOrWhiteSpace(OutputMode) ? "dir" : OutputMode.Trim());

    private string EffectiveDecodeMode =>
        CliText("decode-mode") ?? (string.IsNullOrWhiteSpace(DecodeMode) ? "auto" : DecodeMode.Trim());

    private string EffectivePreset => CliText("preset") ?? Preset;

    private string EffectiveOutput => CliText("output") ?? Output ?? "";

    private string? EffectiveHwaccelValue => CliText("hwaccel") ?? EffectiveHwaccel;

    private bool Flag(string name, bool uiValue)
    {
        var raw = ParsedArgs.Get(name);
        if (raw is null) return uiValue;
        return !string.Equals(raw, "false", StringComparison.OrdinalIgnoreCase);
    }

    private string? CliText(string name) => ParsedArgs.Get(name);

    private long CliBitrate(string name)
    {
        var raw = ParsedArgs.Get(name);
        if (string.IsNullOrWhiteSpace(raw)) return 0;
        try { return Helper.ParseBitrate(raw); }
        catch { return 0; }
    }

    private double CliNumber(string name)
    {
        var raw = ParsedArgs.Get(name);
        if (string.IsNullOrWhiteSpace(raw)) return 0;
        return double.TryParse(raw, NumberStyles.Float, CultureInfo.InvariantCulture, out var v) ? v : 0;
    }

    /// <summary>读取扫描过滤参数（供 UI 与编排层共用）。</summary>
    public void RefreshScanFilters()
    {
        Include = CliText("include");
        Exclude = CliText("exclude");
        Regex = CliText("regex");
        Start = (int)CliNumber("start");
        var count = CliNumber("count");
        Count = count > 0 ? (int)count : int.MaxValue;
    }

    /// <summary>→ entry.Argv（override / strict / debug / anime / hwaccel / decodeMode / output）。</summary>
    public ArgvOptions ToArgvOptions() => new()
    {
        DecodeMode = EffectiveDecodeMode,
        Hwaccel = EffectiveHwaccelValue,
        Strict = Flag("strict", Strict),
        Override = Flag("override", Override),
        Debug = Flag("debug", Debug),
        Anime = Flag("anime", Anime),
        Output = EffectiveOutput,
        OutputMode = EffectiveOutputMode,
        ErrorFile = null,
    };

    /// <summary>→ FfmpegTask.BuildCliTask 的依赖（output / outputMode）。</summary>
    public TaskDeps ToTaskDeps() => new()
    {
        Output = EffectiveOutput,
        OutputMode = EffectiveOutputMode,
    };

    /// <summary>
    /// → FFmpegPresets.CreateFromArgv 的入参。
    ///
    /// 先落界面控件，再落 CLI 参数（CLI 优先）；<c>--ffargs</c> 最后应用，
    /// 保持与 mediac「命令行单独参数 &gt; ffargs 复合参数」一致的相对次序。
    /// </summary>
    public FFmpegPresets.ArgvShim ToArgvShim()
    {
        var shim = new FFmpegPresets.ArgvShim
        {
            Preset = EffectivePreset,
            Anime = Flag("anime", Anime),
            Prefix = CliText("prefix"),
            Suffix = CliText("suffix"),
            Metadata = CliText("metadata"),
            VideoCodec = CliText("video-codec"),
            AudioCodec = CliText("audio-codec"),
            VideoCopy = Flag("video-copy", false),
            AudioCopy = Flag("audio-copy", false),
            VideoBitrate = CliBitrate("video-bitrate"),
            AudioBitrate = CliBitrate("audio-bitrate"),
            VideoQuality = CliNumber("video-quality"),
            AudioQuality = CliNumber("audio-quality"),
            Dimension = (long)CliNumber("dimension"),
            Speed = CliNumber("speed"),
            Framerate = CliNumber("fps"),
        };

        // --ffargs：走既有白名单路径（含分隔符归一与无效写法拦截）
        var ffargs = CliText("ffargs");
        if (!string.IsNullOrWhiteSpace(ffargs))
        {
            var normalized = FfargsValidator.Parse(ffargs).Normalized;
            if (!string.IsNullOrWhiteSpace(normalized))
            {
                FFmpegPresets.ApplyFfargs(shim, FFmpegPresets.ParseFfargs(normalized));
            }
        }

        return shim;
    }

    /// <summary>
    /// CLI 参数覆盖了哪些界面控件。
    ///
    /// 用于在日志里如实说明「你填的参数覆盖了某个下拉框」——
    /// 否则用户改了参数框却看到控件值没变，会以为参数没生效。
    /// </summary>
    public IReadOnlyList<string> CollectOverrideNotes()
    {
        var notes = new List<string>();
        foreach (var opt in CliOptions.All)
        {
            if (opt.ShadowedBy is null) continue;
            if (!ParsedArgs.Has(opt.Name)) continue;
            notes.Add($"参数「--{opt.Name}」覆盖了界面控件 {opt.ShadowedBy}。");
        }
        return notes;
    }

    /// <summary>
    /// 全部需要展示给用户的告警：CLI 解析告警 + ffargs 校验告警 + 覆盖说明。
    ///
    /// 注意必须包含 <c>--ffargs</c> 的校验结果：那些告警在 <see cref="ToArgvShim"/>
    /// 里被用于归一，若这里不带上就等于白算——用户填了 <c>vb=3M</c> 仍会被静默丢弃。
    /// </summary>
    public IReadOnlyList<string> AllWarnings()
    {
        var list = new List<string>(ParsedArgs.Warnings);

        var ffargs = CliText("ffargs");
        if (!string.IsNullOrWhiteSpace(ffargs))
        {
            list.AddRange(FfargsValidator.Parse(ffargs).Warnings);
        }

        list.AddRange(CollectOverrideNotes());
        return list;
    }
}
