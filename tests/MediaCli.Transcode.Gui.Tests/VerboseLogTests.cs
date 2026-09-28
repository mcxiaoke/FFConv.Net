using MediaCli.Transcode.Gui;
using Xunit;

namespace MediaCli.Transcode.Gui.Tests;

/// <summary>
/// 「详细日志」开关是否真正作用于日志过滤的测试。
///
/// 背景：core 在 Debug 下把 ffmpeg 切到 <c>repeat+level+info</c>（stderr 量很大），
/// 但早期 <see cref="TranscodeSession"/> 只调用 <c>FfmpegLogFilter.ShouldKeep</c>、
/// 从未读取该开关，导致用户勾选后几乎看不到额外输出，
/// 而统计块还会显示"已隐藏 N 行（勾选详细日志可全部显示）"——提示与事实矛盾。
/// </summary>
public class VerboseLogTests
{
    /// <summary>一条典型 ffmpeg 常规 verbose 行（默认应被抑制）。</summary>
    private const string VerboseLine =
        "frame=  120 fps= 60 q=28.0 size=    1024kB time=00:00:02.00 speed=1.0x";

    private const string ErrorLine = "[error] Error while opening encoder for output stream";

    private static (List<(SessionLogLevel Level, string Text)> Logs, TranscodeSession Session)
        MakeSession(bool verbose)
    {
        var logs = new List<(SessionLogLevel, string)>();
        var session = new TranscodeSession((lvl, text) => logs.Add((lvl, text)), null, verboseLog: verbose);
        return (logs, session);
    }

    [Fact]
    public void WithoutVerbose_RoutineLinesAreSuppressedAndErrorsKept()
    {
        var (logs, session) = MakeSession(verbose: false);

        session.ForwardCoreLogForTest(VerboseLine);
        session.ForwardCoreLogForTest(ErrorLine);

        Assert.Single(logs);
        Assert.Contains(ErrorLine, logs[0].Text);
        Assert.Equal(1, session.SuppressedLines);   // 常规行被抑制并计数
    }

    [Fact]
    public void WithVerbose_RoutineLinesAreKeptAndNothingIsSuppressed()
    {
        var (logs, session) = MakeSession(verbose: true);

        session.ForwardCoreLogForTest(VerboseLine);
        session.ForwardCoreLogForTest(ErrorLine);

        Assert.Equal(2, logs.Count);
        Assert.Contains(VerboseLine, logs[0].Text);
        Assert.Contains(ErrorLine, logs[1].Text);
        Assert.Equal(0, session.SuppressedLines);   // 勾选后不再"抑制"，与统计块说明一致
    }

    [Fact]
    public void StructuredCoreLines_AreAlwaysForwardedRegardlessOfVerbose()
    {
        foreach (var verbose in new[] { false, true })
        {
            var (logs, session) = MakeSession(verbose);
            session.ForwardCoreLogForTest("[CMD] ffmpeg -i in.mp4 out.mp4");
            session.ForwardCoreLogForTest("[DONE] out.mp4 (1.00MB=>0.50MB)");
            session.ForwardCoreLogForTest("[PREPARE] sz:1.00MB,ts:0s");

            Assert.Equal(3, logs.Count);
            Assert.Equal(0, session.SuppressedLines);   // core 结构化行不属于"被隐藏的常规输出"
        }
    }
}
