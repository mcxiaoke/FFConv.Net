using MediaCli.Transcode.Gui;
using Xunit;

namespace MediaCli.Transcode.Gui.Tests;

/// <summary>
/// ffmpeg 日志过滤的测试（需求 3：不显示 verbose，除非 error/warning）。
///
/// 最关键的一条是「过滤器必须是 core 错误提取口径的超集」：
/// 否则会出现界面报失败、日志里却找不到原因——比显示噪声更糟。
/// </summary>
public class FfmpegLogFilterTests
{
    [Theory]
    [InlineData("[error] Error while opening encoder for output stream")]
    [InlineData("[warning] Trailing option(s) found in the command")]
    [InlineData("Error opening output file out.mp4")]
    [InlineData("Unknown encoder 'bogus_encoder'")]
    [InlineData("Invalid argument")]
    [InlineData("Conversion failed!")]
    [InlineData("Cannot allocate memory")]
    [InlineData("No such file or directory")]
    [InlineData("Unsupported codec")]
    [InlineData("Permission denied")]
    public void ShouldKeep_KeepsErrorsAndWarnings(string line) =>
        Assert.True(FfmpegLogFilter.ShouldKeep(line), $"应保留: {line}");

    [Theory]
    [InlineData("frame=  120 fps= 60 q=28.0 size=    1024kB time=00:00:02.00 bitrate=4194.3kbits/s speed=1.0x")]
    [InlineData("Stream #0:0: Video: h264 (High), yuv420p, 1920x1080, 5000 kb/s")]
    [InlineData("Output #0, mp4, to 'out.mp4':")]
    [InlineData("Press [q] to stop, [?] for help")]
    [InlineData("  libx265 - H.265/HEVC (8 bit) encoder")]
    [InlineData("Metadata: encoder : Lavf61.1.100")]
    public void ShouldKeep_DropsRoutineVerbose(string line) =>
        Assert.False(FfmpegLogFilter.ShouldKeep(line), $"应丢弃: {line}");

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(null)]
    public void ShouldKeep_DropsBlank(string? line) =>
        Assert.False(FfmpegLogFilter.ShouldKeep(line));

    [Fact]
    public void Filter_KeepsOnlyErrors()
    {
        var lines = new[]
        {
            "frame= 1 fps=1",
            "[error] something broke",
            "Stream #0:0 Video",
            "[warning] beware",
        };
        Assert.Equal(["[error] something broke", "[warning] beware"], FfmpegLogFilter.Filter(lines));
    }

    [Fact]
    public void CountSuppressed_CountsOnlyNonBlankDropped()
    {
        var lines = new[] { "frame=1", "", "  ", "[error] x" };
        Assert.Equal(1, FfmpegLogFilter.CountSuppressed(lines));
    }

    /// <summary>勾选「详细日志」时原样放行，便于排障。</summary>
    [Fact]
    public void Apply_KeepEverything_PassesAllLines()
    {
        const string verbose = "frame=  120 fps= 60 speed=1.0x";
        Assert.Equal(verbose, FfmpegLogFilter.Apply(verbose, keepEverything: true));
        Assert.Null(FfmpegLogFilter.Apply(verbose, keepEverything: false));
    }

    /// <summary>
    /// 口径校验：core 用于判定失败的每个特征词，过滤器都必须放行。
    /// 若这条失败，说明过滤比 core 更严，会出现「报失败但日志无原因」。
    /// </summary>
    [Theory]
    [InlineData("error")]
    [InlineData("invalid")]
    [InlineData("failed")]
    [InlineData("cannot")]
    [InlineData("could not")]
    [InlineData("unable")]
    [InlineData("unsupported")]
    [InlineData("not supported")]
    [InlineData("no such")]
    [InlineData("denied")]
    [InlineData("corrupt")]
    [InlineData("missing")]
    [InlineData("out of range")]
    [InlineData("exceed")]
    [InlineData("truncat")]
    public void ShouldKeep_CoversCoreErrorVocabulary(string keyword)
    {
        // core 的 ExtractFFmpegError 用这些词识别错误行
        Assert.True(FfmpegLogFilter.ShouldKeep($"some line with {keyword} inside"));
    }
}
