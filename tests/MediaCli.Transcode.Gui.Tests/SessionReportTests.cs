using MediaCli.Transcode.Gui;
using Xunit;

namespace MediaCli.Transcode.Gui.Tests;

/// <summary>
/// 任务统计块与使用说明文本的测试（需求 6、5）。
///
/// 这些都是纯文本生成，不依赖界面，因此可直接断言内容——
/// 用户看到的统计信息与说明文字必须与实际行为一致，不能各自漂移。
/// </summary>
public class SessionReportTests
{
    private static SessionSummary SampleSummary() => new()
    {
        Total = 200,
        Success = 118,
        Failed = 2,
        Skipped = 3,
        Preview = 0,
        ElapsedMs = 754_000,
        InputBytes = 12_000_000_000,
        OutputBytes = 3_000_000_000,
        OutputCount = 118,
    };

    // ------------------------------------------------------------------
    // 时长格式化
    // ------------------------------------------------------------------

    [Theory]
    [InlineData(0, "0.0 秒")]
    [InlineData(1500, "1.5 秒")]
    [InlineData(59_000, "59.0 秒")]
    [InlineData(60_000, "1 分 0 秒")]
    [InlineData(754_000, "12 分 34 秒")]
    [InlineData(3_723_000, "1 时 2 分 3 秒")]
    public void FormatDuration_IsHumanReadable(long ms, string expected) =>
        Assert.Equal(expected, SessionReport.FormatDuration(ms));

    [Fact]
    public void FormatDuration_NegativeIsMarked() =>
        Assert.Equal("—", SessionReport.FormatDuration(-1));

    // ------------------------------------------------------------------
    // 统计块内容（需求：日志文件位置、输入输出目录、总耗时、文件大小）
    // ------------------------------------------------------------------

    [Fact]
    public void Build_ContainsAllRequiredFields()
    {
        var text = SessionReport.Build(
            SampleSummary(),
            inputDir: @"F:\in",
            outputDir: @"F:\out",
            logPath: @"C:\Temp\mediac-1.log",
            syncedPath: @"F:\out\mediac-transcode-1.log");

        Assert.Contains("任务统计", text);
        Assert.Contains(@"输入目录：F:\in", text);
        Assert.Contains(@"输出目录：F:\out", text);
        Assert.Contains("总耗时：12 分 34 秒", text);
        Assert.Contains("输入大小：", text);
        Assert.Contains("输出大小：", text);
        Assert.Contains(@"日志文件：C:\Temp\mediac-1.log", text);
        Assert.Contains(@"同步副本：F:\out\mediac-transcode-1.log", text);
    }

    [Fact]
    public void Build_CountsOnlyMentionedCategories()
    {
        var text = SessionReport.Build(SampleSummary(), null, null, null, null);
        Assert.Contains("共 200 个", text);
        Assert.Contains("成功 118", text);
        Assert.Contains("失败 2", text);
        Assert.Contains("跳过 3", text);
        // 未出现的类别不列出，避免一堆 0 干扰阅读
        Assert.DoesNotContain("预览", text);
        Assert.DoesNotContain("取消", text);
    }

    /// <summary>输出大小要给出与输入的比值，便于判断压缩效果。</summary>
    [Fact]
    public void Build_OutputSizeShowsRatio()
    {
        var text = SessionReport.Build(SampleSummary(), null, null, null, null);
        Assert.Contains("为输入的 25.0%", text);
    }

    [Fact]
    public void Build_PreviewMode_ExplainsNoOutput()
    {
        var s = new SessionSummary { Total = 1, Preview = 1, InputBytes = 1000, ElapsedMs = 100 };
        var text = SessionReport.Build(s, null, null, null, null);
        Assert.Contains("预览模式未产出文件", text);
    }

    /// <summary>无法取得的项如实标注，不编造数字。</summary>
    [Fact]
    public void Build_MissingValues_AreMarked()
    {
        var s = new SessionSummary { Total = 0 };
        var text = SessionReport.Build(s, null, null, null, null);
        Assert.Contains("（未指定）", text);
        Assert.Contains("（未能创建）", text);
        Assert.Contains("—", text);
    }

    [Fact]
    public void Build_OutputDirNotSet_ExplainsFallback()
    {
        var text = SessionReport.Build(SampleSummary(), @"F:\in", null, null, null);
        Assert.Contains("输出到源文件目录", text);
    }

    /// <summary>失败清单要能直接指向问题文件，省去在长日志里翻找。</summary>
    [Fact]
    public void Build_ListsFailedFiles()
    {
        var s = SampleSummary();
        s.Results.Add(new SessionFileResult
        {
            Name = "broken.mkv", Path = @"F:\in\broken.mkv",
            Outcome = SessionFileOutcome.Failed, Stage = "execute", Detail = "Unknown encoder",
        });
        var text = SessionReport.Build(s, null, null, null, null);

        Assert.Contains("失败文件：", text);
        Assert.Contains("broken.mkv", text);
        Assert.Contains("Unknown encoder", text);
    }

    [Fact]
    public void Build_NoFailures_OmitsFailureSection()
    {
        var s = new SessionSummary { Total = 1, Success = 1, ElapsedMs = 100 };
        var text = SessionReport.Build(s, null, null, null, null);
        Assert.DoesNotContain("失败文件：", text);
    }

    [Fact]
    public void Build_SuppressedLines_AreReported()
    {
        var s = SampleSummary();
        s.SuppressedLines = 4321;
        var text = SessionReport.Build(s, null, null, null, null);
        Assert.Contains("已隐藏 ffmpeg 常规输出 4321 行", text);
        Assert.Contains("详细日志", text);
    }

    /// <summary>日志写盘失败要如实说明，而不是假装日志已生成。</summary>
    [Fact]
    public void Build_LogError_IsReported()
    {
        var text = SessionReport.Build(SampleSummary(), null, null, null, null, logError: "拒绝访问");
        Assert.Contains("日志写盘告警：拒绝访问", text);
    }

    [Fact]
    public void Build_AverageIsOnlyShownWithData()
    {
        var withData = SessionReport.Build(SampleSummary(), null, null, null, null);
        Assert.Contains("平均", withData);

        var noData = SessionReport.Build(new SessionSummary { Total = 0 }, null, null, null, null);
        Assert.DoesNotContain("平均", noData);
    }
}

/// <summary>
/// 使用说明文本的测试（需求 5：预设与 CLI 参数说明集中到「使用说明」）。
/// </summary>
public class AboutContentTests
{
    [Fact]
    public void Presets_ExcludeBasePresets()
    {
        var names = AboutContent.Presets().Select(p => p.Name).ToList();
        Assert.NotEmpty(names);
        Assert.DoesNotContain(names, n => n.StartsWith('_'));
        Assert.Contains("hevc_2k", names);
    }

    [Fact]
    public void PresetDetail_ShowsKeyParameters()
    {
        var preset = AboutContent.Presets().First(p => p.Name == "hevc_2k");
        var text = AboutContent.PresetDetail(preset);

        Assert.Contains("预设名：hevc_2k", text);
        Assert.Contains("类型：video", text);
        Assert.Contains("目标长边：1920", text);
        Assert.Contains("视频质量：23", text);
    }

    /// <summary>说明必须与参数表同源，否则会与实际行为漂移。</summary>
    [Fact]
    public void CliOptionTable_CoversEveryOption()
    {
        var text = AboutContent.CliOptionTable();
        foreach (var o in CliOptions.All)
        {
            Assert.Contains($"--{o.Name}", text);
        }
        Assert.Contains("可用参数", text);
        Assert.Contains("本 GUI 不支持", text);
    }

    [Fact]
    public void CliOptionTable_UnsupportedEntriesExplainWhy()
    {
        var text = AboutContent.CliOptionTable();
        Assert.Contains("原因：", text);
    }

    /// <summary>提示里必须点明码率的正确写法——这是最容易踩的坑。</summary>
    [Fact]
    public void UsageTips_ExplainsBitrateAndPriority()
    {
        var text = AboutContent.UsageTips();
        Assert.Contains("参数优先级", text);
        Assert.Contains("命令行单独参数", text);
        Assert.Contains("3000000", text);
        Assert.Contains("vb=3M", text);
        Assert.Contains("会被 core 静默丢弃", text);
        Assert.Contains("自动换行", text);
    }
}
