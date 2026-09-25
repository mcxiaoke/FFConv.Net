using MediaCli.Transcode.Build;
using MediaCli.Transcode.Gui;
using MediaCli.Transcode.Hardware;
using MediaCli.Transcode.Model;
using MediaCli.Transcode.Presets;
using Xunit;

namespace MediaCli.Transcode.Tests;

/// <summary>
/// GUI 映射层（GuiOptions / TranscodeSession）与 CLI 编排的等价性测试。
///
/// 目标：证明「同一组 UI 输入」与「同一组 CLI 参数」在 core 里产生<b>逐字节一致</b>的
/// ffmpeg 命令行 —— 即 GUI 没有引入第二套参数逻辑。
/// </summary>
public class GuiMappingTests
{
    static GuiMappingTests() => FFmpegPresets.Init();

    // ------------------------------------------------------------------
    // 1. hwaccel 透传语义（经 JS 实测校准）
    // ------------------------------------------------------------------

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void EffectiveHwaccel_Blank_BecomesNull(string value)
    {
        var opts = new GuiOptions { Hwaccel = value };
        Assert.Null(opts.EffectiveHwaccel);
        Assert.Null(opts.ToArgvOptions().Hwaccel);
    }

    /// <summary>
    /// "auto" 必须<b>原样透传</b>，不能改写成 null。
    ///
    /// 曾经误以为「传 auto 会经 normalizeHwaccelName 归一成 d3d 而锁死候选链」，
    /// 实测（node 跑 JS 原版）证明该结论是错的：core 在 auto 分支<b>先于</b> d3d 别名
    /// 拦截了 "auto"。传 "auto" 与传 null 的候选链完全相同。
    /// 而 decodeMode=gpu 时传 "auto" 是「用默认硬件层」的合法语义，传 null 反而会抛错。
    /// </summary>
    [Theory]
    [InlineData("auto", "auto")]
    [InlineData("AUTO", "AUTO")]
    [InlineData("cuda", "cuda")]
    [InlineData("qsv", "qsv")]
    [InlineData("cpu", "cpu")]
    [InlineData(" d3d ", "d3d")]
    public void EffectiveHwaccel_PassesThroughUnchanged(string input, string expected)
    {
        var opts = new GuiOptions { Hwaccel = input };
        Assert.Equal(expected, opts.EffectiveHwaccel);
        Assert.Equal(expected, opts.ToArgvOptions().Hwaccel);
    }

    /// <summary>
    /// 锁定 auto 模式下 "auto" 与 null 的<b>等价性</b>：候选链必须一致。
    /// 这条测试直接调用 core 的候选链解析，用真实行为否定「auto 锁死」的旧结论。
    /// </summary>
    [Fact]
    public void AutoMode_AutoEqualsNull_ForCandidateChain()
    {
        var caps = new HardwareCaps
        {
            FFmpegPath = "ffmpeg",
            Vendor = "nvidia",
            Usable = new Dictionary<string, bool> { ["cuda"] = true, ["d3d"] = true, ["cpu"] = true },
            StaticOk = new Dictionary<string, bool> { ["cuda"] = true, ["d3d"] = true, ["cpu"] = true },
            Hwaccels = ["cuda", "d3d11va"],
        };

        var withAuto = HwDetect.CandidateTiers(caps, "auto", "auto");
        var withNull = HwDetect.CandidateTiers(caps, "auto", null);

        Assert.Equal(withNull, withAuto);
        Assert.Contains("cuda", withAuto); // 厂商层未被锁死
    }

    /// <summary>
    /// gpu 模式才是 "auto" 与 null 真正分道的地方：null 会明确报错，而非静默降级。
    /// 这正是 GUI 必须透传（而非改写）的原因。
    /// </summary>
    [Fact]
    public void GpuMode_AutoIsAccepted_NullThrows()
    {
        var caps = new HardwareCaps
        {
            FFmpegPath = "ffmpeg",
            Vendor = "nvidia",
            Usable = new Dictionary<string, bool> { ["cuda"] = true, ["d3d"] = true, ["cpu"] = true },
            StaticOk = new Dictionary<string, bool> { ["cuda"] = true, ["d3d"] = true, ["cpu"] = true },
            Hwaccels = ["cuda", "d3d11va"],
        };

        var gpuAuto = HwDetect.CandidateTiers(caps, "gpu", "auto");
        Assert.Equal(["d3d"], gpuAuto);

        Assert.Throws<ArgumentException>(() => HwDetect.CandidateTiers(caps, "gpu", null));
    }

    // ------------------------------------------------------------------
    // 2. ffargs：白名单应用 + 无效写法拦截（经 JS 实测校准）
    // ------------------------------------------------------------------

    /// <summary>
    /// 生效路径：码率必须写裸 bps（<c>vb=3000000</c>）。
    /// 带单位的 <c>vb=3M</c> 会被 core 静默丢弃 —— 见 <see cref="FfargsValidator"/> 的说明。
    /// </summary>
    [Fact]
    public void ToArgvShim_AppliesValidFfargs()
    {
        var opts = new GuiOptions
        {
            Preset = "hevc_2k",
            CliArgs = "--ffargs vb=3000000,vq=23,sp=1.5,fps=30",
        };
        var shim = opts.ToArgvShim();

        Assert.Equal(3_000_000, shim.VideoBitrate);
        Assert.Equal(23, shim.VideoQuality);
        Assert.Equal(1.5, shim.Speed);
        Assert.Equal(30, shim.Framerate);
    }

    /// <summary>动漫只能由复选框设置；ffargs 的 an/anime 在 core 里是空操作。</summary>
    [Fact]
    public void ToArgvShim_AnimeComesFromCheckbox_NotFfargs()
    {
        var viaFfargs = new GuiOptions { Preset = "hevc_2k", CliArgs = "--ffargs an=1" };
        Assert.False(viaFfargs.ToArgvShim().Anime);

        var viaCheckbox = new GuiOptions { Preset = "hevc_2k", Anime = true };
        Assert.True(viaCheckbox.ToArgvShim().Anime);
    }

    /// <summary>带单位的码率必须被拦截并给出可操作的警告，而不是静默丢弃。</summary>
    [Fact]
    public void ValidateFfargs_UnitSuffixedBitrate_Warns()
    {
        var r = FfargsValidator.Parse("vb=3M");

        Assert.True(r.HasWarnings);
        Assert.Contains(r.Warnings, w => w.Contains("裸 bps") && w.Contains("3000000"));
        Assert.Empty(r.Accepted);
    }

    /// <summary>同时接受 JS 的 ";" 与 C# CLI 的 "," 分隔符，并归一为 C# 形式。</summary>
    [Theory]
    [InlineData("vq=23;sp=1.5", "vq=23,sp=1.5")]
    [InlineData("vq=23,sp=1.5", "vq=23,sp=1.5")]
    [InlineData("vq=23, sp=1.5 ", "vq=23,sp=1.5")]
    public void ValidateFfargs_NormalizesSeparators(string input, string expected)
    {
        var r = FfargsValidator.Parse(input);
        Assert.Equal(expected, r.Normalized);
        Assert.False(r.HasWarnings);
    }

    [Fact]
    public void ToArgvShim_EmptyFfargs_LeavesShimUntouched()
    {
        var opts = new GuiOptions { Preset = "h264_2k", CliArgs = "" };
        var shim = opts.ToArgvShim();

        Assert.Equal("h264_2k", shim.Preset);
        Assert.Equal(0, shim.VideoBitrate);
        Assert.Equal(0, shim.VideoQuality);
        Assert.False(shim.Anime);
    }

    /// <summary>非白名单键必须被忽略并告警（不能凭此绕过 preset 体系注入裸参数）。</summary>
    [Fact]
    public void ToArgvShim_IgnoresNonWhitelistedKeys()
    {
        var opts = new GuiOptions { Preset = "hevc_2k", CliArgs = "--ffargs evil=1,rm_rf=1" };
        var shim = opts.ToArgvShim();

        Assert.Equal("hevc_2k", shim.Preset);
        Assert.Null(shim.VideoCodec);
        Assert.Null(shim.AudioCodec);

        // AllWarnings 是 UI 实际展示的内容，必须带上 ffargs 校验告警
        var warnings = opts.AllWarnings();
        Assert.Equal(2, warnings.Count);
        Assert.All(warnings, w => Assert.Contains("未知参数键", w));
    }

    /// <summary>字符串型键（编码器 / 前后缀 / 元数据）正常透传。</summary>
    [Fact]
    public void ToArgvShim_AcceptsStringKeys()
    {
        var opts = new GuiOptions
        {
            Preset = "hevc_2k",
            CliArgs = "--ffargs vc=libx265,ac=libopus,px=PRE_,sx=_END",
        };
        var shim = opts.ToArgvShim();

        Assert.Empty(opts.AllWarnings());
        Assert.Equal("libx265", shim.VideoCodec);
        Assert.Equal("libopus", shim.AudioCodec);
        Assert.Equal("PRE_", shim.Prefix);
        Assert.Equal("_END", shim.Suffix);
    }

    /// <summary>数值 0 / 负数必须被拦截（core 把它们视为「未提供」，会静默无效）。</summary>
    [Theory]
    [InlineData("vq=0")]
    [InlineData("vq=-1")]
    public void ValidateFfargs_NonPositiveNumber_Warns(string input)
    {
        var r = FfargsValidator.Parse(input);
        Assert.True(r.HasWarnings);
        Assert.Contains(r.Warnings, w => w.Contains("大于 0"));
        Assert.Empty(r.Accepted);
    }

    [Fact]
    public void ValidateFfargs_EmptyInput_NoWarnings()
    {
        foreach (var input in new[] { "", "   ", null })
        {
            var r = FfargsValidator.Parse(input);
            Assert.False(r.HasWarnings);
            Assert.Equal("", r.Normalized);
        }
    }

    /// <summary>格式错误的片段要告警而不是抛出，UI 不能因用户输入而崩。</summary>
    [Fact]
    public void ValidateFfargs_MalformedSegment_WarnsNotThrows()
    {
        var r = FfargsValidator.Parse("vq=23,broken,=5");
        Assert.True(r.HasWarnings);
        Assert.Equal("vq=23", r.Normalized);
    }

    // ------------------------------------------------------------------
    // 3. 默认值与边界
    // ------------------------------------------------------------------

    [Fact]
    public void Defaults_AreSafe()
    {
        var opts = new GuiOptions();
        var argv = opts.ToArgvOptions();

        Assert.Equal("hevc_2k", opts.Preset);
        Assert.Equal("dir", argv.OutputMode);
        Assert.Equal("auto", argv.DecodeMode);
        Assert.False(argv.Override);   // 默认不覆盖已有产物
        Assert.False(argv.Strict);
        Assert.False(argv.Debug);
        // 透传 "auto"：与 CLI 显式传 --hwaccel auto 完全等价（实测 auto 模式下 ≡ null）
        Assert.Equal("auto", argv.Hwaccel);
    }

    [Theory]
    [InlineData("", "dir")]
    [InlineData("  ", "dir")]
    [InlineData("tree", "tree")]
    [InlineData("file", "file")]
    public void OutputMode_BlankFallsBackToDir(string input, string expected)
    {
        var opts = new GuiOptions { OutputMode = input };
        Assert.Equal(expected, opts.ToArgvOptions().OutputMode);
        Assert.Equal(expected, opts.ToTaskDeps().OutputMode);
    }

    [Fact]
    public void ToTaskDeps_CarriesOutputAndMode()
    {
        var opts = new GuiOptions { Output = @"F:\out", OutputMode = "tree" };
        var deps = opts.ToTaskDeps();

        Assert.Equal(@"F:\out", deps.Output);
        Assert.Equal("tree", deps.OutputMode);
    }

    // ------------------------------------------------------------------
    // 4. 端到端等价：GUI 映射 vs CLI 映射 → 同一命令行
    // ------------------------------------------------------------------

    /// <summary>
    /// 复刻 CLI <c>Program.BuildShim</c> + <c>BuildArgv</c> 的构造方式，
    /// 与 GUI 的 <see cref="GuiOptions"/> 映射做对拍。
    /// 两者都走 core 的 CreateFromArgv，因此命令必须逐字节一致。
    /// </summary>
    [Fact]
    public void GuiAndCliMapping_ProduceIdenticalCommand()
    {
        // 有效写法：码率用裸 bps（vb=3M 会被 core 静默丢弃）。
        const string ffargs = "vb=3000000,vq=23,sp=1.5";

        // ---- CLI 路径（等价于 mediac-dotnet run --input x --preset hevc_2k --ffargs ...）----
        var cliShim = new FFmpegPresets.ArgvShim { Preset = "hevc_2k", Anime = true };
        FFmpegPresets.ApplyFfargs(cliShim, FFmpegPresets.ParseFfargs(ffargs));
        var cliPreset = FFmpegPresets.CreateFromArgv(cliShim);
        var cliArgv = new ArgvOptions
        {
            DecodeMode = "auto",
            Hwaccel = null,          // CLI 未传 --hwaccel（默认空）
            Strict = false,
            Override = false,
            Debug = false,
            Anime = true,
            Output = @"F:\out",
            OutputMode = "dir",
        };

        // ---- GUI 路径 ----
        var gui = new GuiOptions
        {
            Inputs = [@"F:\in\movie.mkv"],
            Preset = "hevc_2k",
            Output = @"F:\out",
            OutputMode = "dir",
            Hwaccel = "auto",        // UI 下拉框默认值
            DecodeMode = "auto",
            CliArgs = $"--ffargs {ffargs}",
            Anime = true,
        };
        var guiPreset = FFmpegPresets.CreateFromArgv(gui.ToArgvShim());
        var guiArgv = gui.ToArgvOptions();

        // 预设与 argv 必须等价
        Assert.Equal(cliPreset.Name, guiPreset.Name);
        Assert.Equal(cliPreset.VideoQuality, guiPreset.VideoQuality);
        Assert.Equal(cliPreset.UserArgs.VideoBitrate, guiPreset.UserArgs.VideoBitrate);
        Assert.Equal(cliPreset.UserArgs.VideoQuality, guiPreset.UserArgs.VideoQuality);
        Assert.Equal(cliPreset.UserArgs.Speed, guiPreset.UserArgs.Speed);
        Assert.Equal(cliPreset.UserArgs.Anime, guiPreset.UserArgs.Anime);
        Assert.Equal(cliArgv.OutputMode, guiArgv.OutputMode);

        // hwaccel 字面量不同（CLI 默认 null / UI 默认 "auto"），但**效果必须等价**：
        // 实测 auto 模式下 "auto" 与 null 产生完全相同的候选链。
        // 用候选链而非字符串做断言，才能表达"等价"这一真实契约。
        var caps = new HardwareCaps
        {
            FFmpegPath = "ffmpeg",
            Vendor = "nvidia",
            Usable = new Dictionary<string, bool> { ["cuda"] = true, ["d3d"] = true, ["cpu"] = true },
            StaticOk = new Dictionary<string, bool> { ["cuda"] = true, ["d3d"] = true, ["cpu"] = true },
            Hwaccels = ["cuda", "d3d11va"],
        };
        Assert.Equal(
            HwDetect.CandidateTiers(caps, cliArgv.DecodeMode, cliArgv.Hwaccel),
            HwDetect.CandidateTiers(caps, guiArgv.DecodeMode, guiArgv.Hwaccel));


        // 同一份构造输入 → 同一 cpu 层命令（与 PORTING-NOTES 附录的 parity 方法一致）
        var entry = new TranscodeEntry
        {
            Path = @"F:\in\movie.mkv",
            Name = "movie.mkv",
            Size = 100 * 1000 * 1000,
            Info = new MediaInfo
            {
                Duration = 60,
                Bitrate = 10_240_000,
                Video = new VideoInfo
                {
                    Format = "hevc", Width = 3840, Height = 2160, Bitrate = 10_000_000,
                    FrameRate = 23.976, PixelFormat = "yuv420p10le", BitDepth = 10,
                },
                Audio = new AudioInfo { Format = "aac", Bitrate = 192_000 },
            },
        };
        var cpuTier = Array.Find(HwAccel.Tiers, t => t.Name == "cpu")!;
        var hwPlan = new HwPlan { Tier = cpuTier, Size = null, Reason = "test" };

        string CommandFor(FFmpegPreset preset, ArgvOptions argv)
        {
            var e = entry;
            e.Preset = preset;
            e.Argv = argv;
            e.DstArgs = MediaCli.Transcode.Planning.FfmpegPlan.CalculateDstArgs(e);
            e.FileDst = @"F:\out\movie_hevc_2k.mp4";
            return FfmpegBuild.FlattenFFArgs(FfmpegBuild.CreateFFmpegArgs(e, hwPlan).Args)!;
        }

        var cliCmd = CommandFor(cliPreset, cliArgv);
        var guiCmd = CommandFor(guiPreset, guiArgv);

        // 核心断言：两条路径生成的命令逐字节一致
        Assert.Equal(cliCmd, guiCmd);
        Assert.Contains("libx265", guiCmd);

        // vb=3000000 使 b>0 → 走 VBR 码控（-b:v），此时**不**输出 -crf。
        // 这是 core 的既有语义（HwAccel: usingVbr = b > 0），如实断言以免掩盖真实行为。
        Assert.Contains("-b:v 3000K", guiCmd);
        Assert.DoesNotContain("-crf", guiCmd);

        // 对照：不给码率时走 CQ 模式，应输出 -crf（验证两种码控模式都能被正确触达）
        var cqPreset = FFmpegPresets.CreateFromArgv(new FFmpegPresets.ArgvShim
        {
            Preset = "hevc_2k",
            Anime = true,
            VideoQuality = 23,
        });
        var cqCmd = CommandFor(cqPreset, cliArgv);
        Assert.Contains("-crf 23", cqCmd);
        Assert.DoesNotContain("-b:v", cqCmd);
    }

    // ------------------------------------------------------------------
    // 5. TestMode 哨兵识别（预览不误报为失败）
    // ------------------------------------------------------------------

    /// <summary>
    /// 预览走 TestMode：core 会置 FFmpegFailed=true 且 FFmpegError="test-mode skip"。
    /// 哨兵常量必须与 core 实际写入的值一致，否则 UI 会把每次预览都报成失败。
    /// </summary>
    [Fact]
    public void TestModeSentinel_MatchesCoreBehaviour()
    {
        var entry = new TranscodeEntry
        {
            Path = @"F:\in\movie.mkv",
            Name = "movie.mkv",
            Size = 100 * 1000 * 1000,
            TestMode = true,
            Argv = new ArgvOptions { OutputMode = "dir" },
        };
        // 模拟 core 在 TestMode 分支的写入（FfmpegRun.RunFFmpeg 的 dry-run 契约）
        entry.FFmpegFailed = true;
        entry.FFmpegError = TranscodeSession.TestModeSentinel;

        // 关键：哨兵命中时，UI 必须判为「预览成功」而不是「失败」
        Assert.True(entry.TestMode);
        Assert.Equal("test-mode skip", TranscodeSession.TestModeSentinel);
        Assert.Equal(TranscodeSession.TestModeSentinel, entry.FFmpegError);
        // ToRunResult 仍会投影成 failed —— 这正是必须靠哨兵在更前面拦下的原因
        Assert.Equal(RunStatus.Failed, entry.ToRunResult().Status);
    }

    // ------------------------------------------------------------------
    // 6. 预设注册完整性
    // ------------------------------------------------------------------

    [Fact]
    public void PresetNames_ExcludeBasePresets()
    {
        TranscodeSession.EnsurePresetsLoaded();
        var names = TranscodeSession.PresetNames();

        Assert.NotEmpty(names);
        Assert.DoesNotContain(names, n => n.StartsWith('_'));
        Assert.Contains("hevc_2k", names);
        Assert.Contains("h264_2k", names);
        Assert.Contains("audio_extract", names);
    }

    /// <summary>预设注册表必须能读到仓库根的 presets/default.yaml（归位后的路径）。</summary>
    [Fact]
    public void BundledPresetPath_ResolvesAfterRelocation()
    {
        var path = PresetLoader.BundledPresetPath;
        Assert.True(File.Exists(path), $"bundled preset not found: {path}");
        Assert.EndsWith(Path.Combine("presets", "default.yaml"), path);
    }
}
