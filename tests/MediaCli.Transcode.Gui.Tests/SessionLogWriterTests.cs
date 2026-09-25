using MediaCli.Transcode.Gui;
using Xunit;

namespace MediaCli.Transcode.Gui.Tests;

/// <summary>
/// 会话日志落盘与系统信息探测的测试（需求 6、7、4）。
/// </summary>
public class SessionLogWriterTests
{
    [Fact]
    public void Constructor_CreatesTempLog()
    {
        using var w = new SessionLogWriter();
        Assert.NotNull(w.TempPath);
        Assert.True(File.Exists(w.TempPath));
        Assert.Null(w.WriteError);
    }

    [Fact]
    public void Write_PersistsToTempFile()
    {
        using var w = new SessionLogWriter();
        w.Write(SessionLogLevel.Info, "第一行", DateTime.Now);
        w.Write(SessionLogLevel.Error, "出错了", DateTime.Now);

        var text = ReadLog(w.TempPath!);
        Assert.Contains("第一行", text);
        Assert.Contains("出错了", text);
        Assert.Contains("×", text);   // 错误级别前缀
        Assert.Equal(2, w.LineCount);
    }

    /// <summary>
    /// 读取日志内容（writer 仍在打开状态）。
    ///
    /// 必须显式用 <c>FileShare.ReadWrite</c>：writer 在转码期间保持文件打开，
    /// 而 <c>File.ReadAllText</c> 的默认共享模式是 <c>Read</c>——它不允许已存在的
    /// 写句柄，于是抛 IOException。这不是产品缺陷：writer 侧刻意允许共享，
    /// 正是为了让人能在转码过程中用编辑器查看日志。
    /// </summary>
    private static string ReadLog(string path)
    {
        using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        using var sr = new StreamReader(fs, System.Text.Encoding.UTF8);
        return sr.ReadToEnd();
    }

    /// <summary>多行内容逐行记录，保持日志文件可读。</summary>
    [Fact]
    public void Write_SplitsMultiLineContent()
    {
        using var w = new SessionLogWriter();
        w.Write(SessionLogLevel.Info, "甲\n乙\n丙", DateTime.Now);
        Assert.Equal(3, w.LineCount);
    }

    [Fact]
    public void Write_IgnoresEmpty()
    {
        using var w = new SessionLogWriter();
        w.Write(SessionLogLevel.Info, "", DateTime.Now);
        w.Write(SessionLogLevel.Info, "   \n  ", DateTime.Now);
        Assert.Equal(0, w.LineCount);
    }

    /// <summary>线程安全：core 的回调来自线程池线程。</summary>
    [Fact]
    public void Write_IsThreadSafe()
    {
        using var w = new SessionLogWriter();
        Parallel.For(0, 200, i => w.Write(SessionLogLevel.Info, $"行 {i}", DateTime.Now));
        Assert.Equal(200, w.LineCount);
    }

    [Fact]
    public void SyncTo_CopiesLogToOutputDir()
    {
        var dir = Path.Combine(Path.GetTempPath(), "ffconvnet-logtest-" + Guid.NewGuid().ToString("N")[..8]);
        try
        {
            using var w = new SessionLogWriter();
            w.Write(SessionLogLevel.Info, "内容甲", DateTime.Now);

            var synced = w.SyncTo(dir);
            Assert.NotNull(synced);
            Assert.True(File.Exists(synced));
            Assert.Contains("内容甲", File.ReadAllText(synced!));
            Assert.Equal(synced, w.SyncedPath);
        }
        finally
        {
            try { if (Directory.Exists(dir)) Directory.Delete(dir, true); } catch { /* ignore */ }
        }
    }

    /// <summary>未请求同步时不产出文件（用户没勾选就不该多写东西）。</summary>
    [Fact]
    public void SyncTo_NullOrBlank_ReturnsNull()
    {
        using var w = new SessionLogWriter();
        Assert.Null(w.SyncTo(null));
        Assert.Null(w.SyncTo(""));
        Assert.Null(w.SyncTo("   "));
        Assert.Null(w.SyncedPath);
    }

    /// <summary>目录不可写时返回 null 并记录原因，而不是抛出中断转码。</summary>
    [Fact]
    public void SyncTo_InvalidTarget_ReportsErrorInsteadOfThrowing()
    {
        using var w = new SessionLogWriter();
        w.Write(SessionLogLevel.Info, "内容", DateTime.Now);

        // 用非法字符构成不可创建的路径
        var bad = Path.Combine(Path.GetTempPath(), "bad\0dir");
        var result = w.SyncTo(bad);
        Assert.Null(result);
        Assert.NotNull(w.WriteError);
    }

    [Fact]
    public void GetAllText_ReturnsAllLines()
    {
        using var w = new SessionLogWriter();
        w.Write(SessionLogLevel.Info, "甲", DateTime.Now);
        w.Write(SessionLogLevel.Info, "乙", DateTime.Now);
        var text = w.GetAllText();
        Assert.Contains("甲", text);
        Assert.Contains("乙", text);
    }

    /// <summary>统计块也写进日志文件，便于随产物归档。</summary>
    [Fact]
    public void Write_AcceptsReportText()
    {
        using var w = new SessionLogWriter();
        var report = SessionReport.Build(new SessionSummary { Total = 1 }, null, null, w.TempPath, null);
        w.Write(SessionLogLevel.Info, report, DateTime.Now);

        var text = ReadLog(w.TempPath!);
        Assert.Contains("任务统计", text);
    }

    [Fact]
    public void Dispose_IsIdempotent()
    {
        var w = new SessionLogWriter();
        w.Dispose();
        w.Dispose();
    }
}

/// <summary>系统信息探测的测试（需求 4：状态栏显示 CPU / GPU / ffmpeg 版本）。</summary>
public class SystemInfoProbeTests
{
    [Fact]
    public void CpuName_IsNotEmpty()
    {
        var name = SystemInfoProbe.CpuName();
        Assert.False(string.IsNullOrWhiteSpace(name));
    }

    /// <summary>版本串要压缩，否则状态栏会被版权声明挤满。</summary>
    [Theory]
    [InlineData("ffmpeg version 8.1.2-full_build-www.gyan.dev Copyright (c) 2000-2026 the FFmpeg developers",
                "8.1.2-full_build")]
    [InlineData("ffmpeg version 7.0 Copyright (c) 2000-2024", "7.0")]
    [InlineData("ffmpeg version n-126733-g1a2b3c", "n-126733-g1a2b3c")]
    [InlineData("8.1.2", "8.1.2")]
    public void ShortVersion_CompressesLongStrings(string input, string expected) =>
        Assert.Equal(expected, SystemInfoProbe.ShortVersion(input));

    [Fact]
    public void ToStatusLine_IncludesAllFields()
    {
        var info = new SystemInfo("Intel i7", "RTX 4070", "8.1.2", "nvidia", ["cuda", "d3d"], ["RTX 4070"]);
        var line = info.ToStatusLine();

        Assert.Contains("CPU Intel i7", line);
        Assert.DoesNotContain("GPU", line);
        Assert.Contains("ffmpeg 8.1.2", line);
        Assert.Contains("cuda/d3d", line);

        // GPU 仍在完整文本中供日志与悬停展示
        Assert.Contains("RTX 4070", info.ToFullText());
    }

    /// <summary>
    /// 状态栏必须紧凑：不显示 GPU（移至日志区与悬停提示）；完整 GPU 清单保留在 ToFullText。
    /// </summary>
    [Fact]
    public void ToStatusLine_TruncatesExtraGpus()
    {
        var info = new SystemInfo(
            "Intel i7", "RTX 4070", "8.1.2", "nvidia", ["cuda"],
            ["RTX 4070", "UHD Graphics 750", "GameViewer Virtual Display"]);

        var line = info.ToStatusLine();
        Assert.DoesNotContain("GPU", line);
        Assert.Contains("ffmpeg 8.1.2", line);          // 版本在可视区内

        // 完整清单保留所有 GPU
        var full = info.ToFullText();
        Assert.Contains("RTX 4070", full);
        Assert.Contains("GameViewer Virtual Display", full);
        Assert.Contains("UHD Graphics 750", full);
    }

    /// <summary>没有可用硬件层时要说明会走 cpu，而不是留空。</summary>
    [Fact]
    public void ToStatusLine_NoHardwareTiers_SaysCpu()
    {
        var info = new SystemInfo("Intel i7", "未识别", "8.1.2", "unknown", [], []);
        Assert.Contains("无（cpu）", info.ToStatusLine());
    }

    /// <summary>CPU 名要去掉商标符号，避免占用状态栏宽度。</summary>
    [Fact]
    public void ShortCpu_StripsTrademarkNoise()
    {
        var shortened = SystemInfo.ShortCpu("11th Gen Intel(R) Core(TM) i7-11700 @ 2.50GHz");
        Assert.DoesNotContain("(R)", shortened);
        Assert.DoesNotContain("(TM)", shortened);
        Assert.Contains("i7-11700", shortened);
    }

    [Fact]
    public void Fallback_ProducesUsableInfo()
    {
        var info = SystemInfoProbe.Fallback(Environment.GetEnvironmentVariable("FFMPEG_PATH"));
        Assert.False(string.IsNullOrWhiteSpace(info.Cpu));
        Assert.False(string.IsNullOrWhiteSpace(info.FfmpegVersion));
    }

    /// <summary>探测失败也必须回调，保证状态栏不会永远停在「探测中」。</summary>
    [Fact]
    public void ProbeAsync_AlwaysInvokesCallback()
    {
        using var done = new ManualResetEventSlim(false);
        SystemInfo? captured = null;

        SystemInfoProbe.ProbeAsync(
            Environment.GetEnvironmentVariable("FFMPEG_PATH"),
            info => { captured = info; done.Set(); });

        Assert.True(done.Wait(TimeSpan.FromSeconds(90)), "探测未在超时内回调");
        Assert.NotNull(captured);
    }
}
