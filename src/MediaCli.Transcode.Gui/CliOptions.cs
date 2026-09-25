namespace MediaCli.Transcode.Gui;

/// <summary>参数取值类型。</summary>
public enum CliValueKind
{
    /// <summary>普通字符串。</summary>
    Text,
    /// <summary>纯数字。</summary>
    Number,
    /// <summary>码率：纯数字(bps) 或带 k/m/g 单位。</summary>
    Bitrate,
    /// <summary>布尔开关，无取值。</summary>
    Flag,
    /// <summary>限定取值集合。</summary>
    Choice,
}

/// <summary>
/// 一条 mediac CLI 参数的定义。
///
/// 这张表是<b>单一事实源</b>：既驱动参数解析与校验，也生成「使用说明」界面，
/// 避免文档与实现漂移。
/// </summary>
/// <param name="Name">规范名（CLI 写法，kebab-case）。</param>
/// <param name="Kind">取值类型。</param>
/// <param name="Description">中文说明。</param>
/// <param name="Aliases">别名（含 mediac 的短别名与 ffargs 别名）。</param>
/// <param name="Choices">Choice 类型的合法取值。</param>
/// <param name="Supported">GUI 是否支持。false 时仅告警、不生效。</param>
/// <param name="Reason">不支持的原因（面向用户）。</param>
/// <param name="ShadowedBy">被哪个界面控件覆盖（该控件优先级更高，与 CLI 一致）。</param>
public sealed record CliOption(
    string Name,
    CliValueKind Kind,
    string Description,
    string[]? Aliases = null,
    string[]? Choices = null,
    bool Supported = true,
    string? Reason = null,
    string? ShadowedBy = null);

/// <summary>
/// mediac CLI 参数表（对应 <c>cmd/cmd_ffmpeg.js</c> 的选项定义）。
///
/// 取舍原则：
/// - <b>能生效的才暴露</b>。core 无法承接的参数（并发、删除源文件等）明确列出并说明原因，
///   而不是静默忽略——静默忽略会让用户以为参数生效了。
/// - 与界面控件重复的参数仍然接受，但<b>控件优先</b>，这与 mediac 的
///   「命令行单独参数 &gt; ffargs 复合参数」优先级一致。
/// </summary>
public static class CliOptions
{
    public static readonly IReadOnlyList<CliOption> All =
    [
        // ---- 与界面控件对应（控件优先） ----
        new("preset", CliValueKind.Text, "转换预设名",
            ShadowedBy: "「Preset」下拉框"),
        new("output", CliValueKind.Text, "输出目录",
            Aliases: ["o"], ShadowedBy: "「输出目录」输入框"),
        new("output-mode", CliValueKind.Choice, "输出目录结构：tree 保持目录树 / dir 保持父目录 / file 扁平化",
            Choices: ["tree", "dir", "file"], ShadowedBy: "「输出模式」单选钮"),
        new("hwaccel", CliValueKind.Text, "硬件加速方式：auto/cuda/qsv/amf/d3d/cpu 等",
            Aliases: ["hw"], ShadowedBy: "「hwaccel」下拉框"),
        new("decode-mode", CliValueKind.Choice, "解码模式：auto/gpu/cpu",
            Choices: ["auto", "gpu", "cpu"], ShadowedBy: "「解码模式」下拉框"),
        new("override", CliValueKind.Flag, "覆盖已存在的输出文件",
            Aliases: ["O"], ShadowedBy: "「覆盖已有」复选框"),
        new("strict", CliValueKind.Flag, "严格模式：禁用一切自动降级，不支持的文件直接跳过",
            ShadowedBy: "「严格模式」复选框"),
        new("debug", CliValueKind.Flag, "输出 ffmpeg 详细信息（日志量会显著增加）",
            ShadowedBy: "「详细日志」复选框"),
        new("anime", CliValueKind.Flag, "动漫调优模式",
            ShadowedBy: "「动漫模式」复选框"),

        // ---- 编码参数 ----
        new("video-bitrate", CliValueKind.Bitrate, "视频目标码率：纯数字(bps) 或带单位，如 3000000 / 3M / 800k",
            Aliases: ["vb", "vbit", "vbk", "vbitrate"]),
        new("video-quality", CliValueKind.Number, "视频质量（CRF/CQ，数值越小质量越高）",
            Aliases: ["vq", "vquality"]),
        new("video-codec", CliValueKind.Text, "显式指定视频编码器，如 hevc_nvenc / libx265 / copy（穿透硬件分层）",
            Aliases: ["vc", "vcodec"]),
        new("video-copy", CliValueKind.Flag, "复制视频流，不重新编码"),
        new("audio-bitrate", CliValueKind.Bitrate, "音频目标码率：纯数字(bps) 或带单位，如 192000 / 192k",
            Aliases: ["ab", "abit", "abk", "abitrate"]),
        new("audio-quality", CliValueKind.Number, "音频质量（VBR）",
            Aliases: ["aq", "aquality"]),
        new("audio-codec", CliValueKind.Text, "音频编码器，如 aac / libopus / copy",
            Aliases: ["ac", "acodec"]),
        new("audio-copy", CliValueKind.Flag, "复制音频流，不重新编码"),
        new("metadata", CliValueKind.Text, "输出元数据，多组用 ; 分隔：title=My Video;comment=hi",
            Aliases: ["md", "meta"]),

        // ---- 命名与画面 ----
        new("prefix", CliValueKind.Text, "输出文件名前缀（支持模板变量，如 {preset}）",
            Aliases: ["px", "pf", "P"]),
        new("suffix", CliValueKind.Text, "输出文件名后缀（支持模板变量）",
            Aliases: ["sx", "sf", "S"]),
        new("dimension", CliValueKind.Number, "目标长边像素（禁止放大）",
            Aliases: ["dm"]),
        new("fps", CliValueKind.Number, "输出帧率",
            Aliases: ["framerate"]),
        new("speed", CliValueKind.Number, "变速倍率 0.5–2.0（音视频同步变速）",
            Aliases: ["sp"]),

        // ---- 文件筛选 ----
        new("include", CliValueKind.Text, "仅处理文件名包含该子串的文件",
            Aliases: ["I"]),
        new("exclude", CliValueKind.Text, "跳过文件名包含该子串的文件",
            Aliases: ["E"]),
        new("regex", CliValueKind.Text, "仅处理文件名匹配该正则的文件（C# core 为模式串，与 Node 版的布尔开关不同）",
            Aliases: ["re"]),
        new("start", CliValueKind.Number, "从第几个文件开始处理（0 起）"),
        new("count", CliValueKind.Number, "最多处理多少个文件"),

        // ---- 复合参数 ----
        new("ffargs", CliValueKind.Text, "复合参数串，逗号分隔：vb=3000000,vq=23,sp=1.5（码率写裸 bps）"),

        // ---- GUI 不支持（明确告警，不静默忽略） ----
        new("jobs", CliValueKind.Number, "并行任务数",
            Aliases: ["j"], Supported: false,
            Reason: "本 GUI 为串行执行（core 的硬件探测结果缓存在静态字段中，并发会互相污染）"),
        new("delete-source-files", CliValueKind.Flag, "转码成功后删除源文件",
            Supported: false, Reason: "GUI 不提供删除源文件这类破坏性操作"),
        new("info", CliValueKind.Flag, "显示媒体文件信息",
            Supported: false, Reason: "媒体信息已包含在日志中"),
        new("auto-confirm", CliValueKind.Flag, "自动确认所有交互提示",
            Aliases: ["A"], Supported: false, Reason: "GUI 为交互式，确认由对话框完成"),
        new("doit", CliValueKind.Flag, "实际执行（否则仅预览）",
            Aliases: ["d"], Supported: false, Reason: "由「预览命令」/「开始转码」按钮决定"),
        new("error-file", CliValueKind.Text, "将错误日志写入文件",
            Supported: false, Reason: "由「同步日志到输出目录」复选框统一处理"),
        new("extensions", CliValueKind.Text, "自定义处理的扩展名列表",
            Aliases: ["e"], Supported: false, Reason: "媒体类型由所选预设决定"),
        new("filelist", CliValueKind.Text, "从文本文件读取待处理文件清单",
            Supported: false, Reason: "请使用「选择文件」按钮"),
        new("show-presets", CliValueKind.Flag, "列出全部预设",
            Supported: false, Reason: "见「Preset」下拉框与「使用说明」"),
    ];

    private static readonly Dictionary<string, CliOption> Lookup = BuildLookup();

    private static Dictionary<string, CliOption> BuildLookup()
    {
        // 必须用 Ordinal（区分大小写）：mediac 的短别名刻意区分大小写，
        // 如 -E 是 exclude、-e 是 extensions；-O 是 override、-o 是 output。
        // 若用 OrdinalIgnoreCase，这些别名会互相覆盖，导致参数解析到错误的目标。
        var map = new Dictionary<string, CliOption>(StringComparer.Ordinal);
        foreach (var opt in All)
        {
            map[opt.Name] = opt;
            if (opt.Aliases is null) continue;
            foreach (var alias in opt.Aliases) map[alias] = opt;
        }
        return map;
    }

    /// <summary>按规范名或别名解析（区分大小写）；未知名返回 null。</summary>
    public static CliOption? Resolve(string nameOrAlias) =>
        Lookup.TryGetValue(nameOrAlias, out var opt) ? opt : null;

    /// <summary>可用于提示的合法参数名（规范名，按字母序）。</summary>
    public static IEnumerable<string> KnownNames() =>
        All.Select(o => o.Name).OrderBy(n => n, StringComparer.Ordinal);
}
