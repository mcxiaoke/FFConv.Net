using System.Globalization;
using MediaCli.Transcode.Model;
using MediaCli.Transcode.Presets;
using MediaCli.Transcode.Run;

namespace MediaCli.Transcode.Gui;

/// <summary>
/// UI 选项 → core 入参的纯映射层。
///
/// 设计约束：本类型不引用任何 WinForms 类型，因此可直接被 xUnit 测试覆盖。
/// 语义与 CLI <c>Program.cs</c> 的 BuildArgv / BuildShim 逐条同构，
/// 保证「同一组输入 → 同一套 core 调用 → 同一条 ffmpeg 命令」。
/// </summary>
public sealed class GuiOptions
{
    /// <summary>输入文件或目录（可多个）。</summary>
    public List<string> Inputs { get; init; } = [];

    /// <summary>预设名。别名（anime / av1 / h264 / hevc_anime …）由 core 解析，此处不重复实现。</summary>
    public string Preset { get; set; } = "hevc_2k";

    /// <summary>输出目录；空字符串表示输出到源文件同目录。</summary>
    public string Output { get; set; } = "";

    /// <summary>tree | dir | file。</summary>
    public string OutputMode { get; set; } = "dir";

    /// <summary>auto | cuda | qsv | amf | d3d | d3d11va | d3d12va | dxva2 | cpu。</summary>
    public string Hwaccel { get; set; } = "auto";

    /// <summary>auto | gpu | cpu。</summary>
    public string DecodeMode { get; set; } = "auto";

    /// <summary>复合参数串，如 <c>vb=3M,vq=23,sp=1.0</c>（走 core 的 ffargs 白名单）。</summary>
    public string Ffargs { get; set; } = "";

    public bool Override { get; set; }
    public bool Strict { get; set; }
    public bool Debug { get; set; }
    public bool Anime { get; set; }

    /// <summary>
    /// 空值归一为 <c>null</c>（表示「未指定」）；其余值<b>原样透传，包括 "auto"</b>。
    ///
    /// 刻意不做 auto→null 的改写。经实测（见 GuiMappingTests）：
    /// - <b>decodeMode=auto</b>：core 的 <c>CandidateTiers</c> 在 auto 分支<b>先于</b>
    ///   <c>normalizeHwaccelName</c> 的 d3d 别名拦截了 "auto"，因此传 "auto" 与传 null
    ///   产生的候选链<b>完全相同</b>（实测均为厂商层 → d3d → cpu）；
    /// - <b>decodeMode=gpu</b>：传 "auto" 表示「用默认硬件层」（JS 的既有语义），传 null 会
    ///   抛「requires --hwaccel」。透传才能与 JS/CLI 保持一致。
    ///
    /// 结论：透传即可，不做任何"聪明"改写，从而与 CLI 零分歧。
    /// </summary>
    public string? EffectiveHwaccel =>
        string.IsNullOrWhiteSpace(Hwaccel) ? null : Hwaccel.Trim();

    private string EffectiveOutputMode =>
        string.IsNullOrWhiteSpace(OutputMode) ? "dir" : OutputMode.Trim();

    private string EffectiveDecodeMode =>
        string.IsNullOrWhiteSpace(DecodeMode) ? "auto" : DecodeMode.Trim();

    /// <summary>→ entry.Argv（override / strict / debug / anime / hwaccel / decodeMode / output）。</summary>
    public ArgvOptions ToArgvOptions() => new()
    {
        DecodeMode = EffectiveDecodeMode,
        Hwaccel = EffectiveHwaccel,
        Strict = Strict,
        Override = Override,
        Debug = Debug,
        Anime = Anime,
        Output = Output ?? "",
        OutputMode = EffectiveOutputMode,
        ErrorFile = null,
    };

    /// <summary>→ FfmpegTask.BuildCliTask 的依赖（output / outputMode）。</summary>
    public TaskDeps ToTaskDeps() => new()
    {
        Output = Output ?? "",
        OutputMode = EffectiveOutputMode,
    };

    /// <summary>
    /// 校验并归一 ffargs 输入框内容。
    /// 凡不能生效的写法都会产生明确警告（详见 <see cref="FfargsValidator"/> 的说明）。
    /// </summary>
    public FfargsValidator.Result ValidateFfargs() => FfargsValidator.Parse(Ffargs);

    /// <summary>
    /// → FFmpegPresets.CreateFromArgv 的入参。
    /// ffargs 先经 <see cref="FfargsValidator"/> 归一（修正分隔符、拦截无效写法），
    /// 再走 core 的 <see cref="FFmpegPresets.ParseFfargs"/> + <see cref="FFmpegPresets.ApplyFfargs"/>，
    /// 别名映射与「0 / false / 空串视为未提供」的优先级修复由 core 承担。
    /// </summary>
    public FFmpegPresets.ArgvShim ToArgvShim()
    {
        var shim = new FFmpegPresets.ArgvShim
        {
            Preset = Preset,
            Anime = Anime,
        };
        var normalized = ValidateFfargs().Normalized;
        if (!string.IsNullOrWhiteSpace(normalized))
        {
            FFmpegPresets.ApplyFfargs(shim, FFmpegPresets.ParseFfargs(normalized));
        }
        return shim;
    }
}
