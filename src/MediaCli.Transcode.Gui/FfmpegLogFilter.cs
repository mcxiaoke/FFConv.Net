using System.Text.RegularExpressions;

namespace MediaCli.Transcode.Gui;

/// <summary>
/// ffmpeg 原始输出的过滤器（需求：日志里不显示 verbose 信息，除非是 error/warning）。
///
/// 背景：core 默认以 <c>-v error</c> 运行，此时 stderr 本就只有错误行；但勾选
/// 「详细日志」后会切到 <c>repeat+level+info</c>，输出量暴涨且绝大多数是无用噪声。
/// 因此这里提供一道过滤：<b>只放行错误/警告，其余丢弃</b>。
///
/// 关键取舍：过滤口径必须与 core 的 <c>ExtractFFmpegError</c> <b>同源</b>，
/// 否则会出现「界面说失败、但日志里找不到原因」——那比显示噪声更糟。
/// 因此特征词表刻意与 core 保持一致。
/// </summary>
public static partial class FfmpegLogFilter
{
    /// <summary>ffmpeg 的日志级别标签，如 <c>[error]</c> / <c>[warning]</c>。</summary>
    [GeneratedRegex(@"\[(error|warning|fatal|panic)\]", RegexOptions.IgnoreCase)]
    private static partial Regex RxLevelTag();

    /// <summary>
    /// 特征词表：<b>必须是 core <c>FfmpegRun.ExtractFFmpegError</c> 的超集</b>。
    ///
    /// 为什么强调"超集"：core 判定失败时会按「[error] 标签 → 特征词行 → 兜底末行」
    /// 三级策略取错误信息。若过滤器的词表更窄，就会出现
    /// 「界面说失败、日志里却找不到那行原因」——这比显示噪声更糟。
    /// 因此在 core 词表基础上补充了若干明确的错误指示词。
    /// </summary>
    [GeneratedRegex(
        @"error|warning|warn|invalid|failed|failure|cannot|could not|unable|unsupported|not supported|" +
        @"no such|denied|corrupt|missing|out of range|exceed|truncat|" +
        @"unknown|unrecognized|not found|forbidden|fatal|abort",
        RegexOptions.IgnoreCase)]
    private static partial Regex RxKeyword();

    /// <summary>这一行是否值得显示给用户。</summary>
    public static bool ShouldKeep(string? line)
    {
        if (string.IsNullOrWhiteSpace(line)) return false;
        return RxLevelTag().IsMatch(line) || RxKeyword().IsMatch(line);
    }

    /// <summary>过滤一串行，只保留错误/警告。</summary>
    public static List<string> Filter(IEnumerable<string> lines) =>
        lines.Where(ShouldKeep).ToList();

    /// <summary>
    /// 按需过滤：<paramref name="keepEverything"/> 为真（勾选「详细日志」）时原样返回。
    /// 返回 null 表示这一行被丢弃。
    /// </summary>
    public static string? Apply(string line, bool keepEverything) =>
        keepEverything || ShouldKeep(line) ? line : null;

    /// <summary>被丢弃的行数统计（用于在日志里给出「已隐藏 N 行」的说明）。</summary>
    public static int CountSuppressed(IEnumerable<string> lines) =>
        lines.Count(l => !string.IsNullOrWhiteSpace(l) && !ShouldKeep(l));
}
