using MediaCli.Transcode.Hardware;
using MediaCli.Transcode.Model;
using MediaCli.Transcode.Presets;
using MediaCli.Transcode.Support;
using Xunit;

namespace MediaCli.Transcode.Tests;

/// <summary>preset_loader.js / hwdetect.js / gpu.js 的等价性测试。</summary>
public class PresetAndDetectTests
{
    [Theory]
    [InlineData("233k", 233_000)]
    [InlineData("4M", 4_000_000)]
    [InlineData("1.5m", 1_500_000)]
    [InlineData("800", 800)]
    [InlineData("3g", 3_000_000_000)]
    public void ParseBitrate_Units(string input, long expected)
    {
        Assert.Equal(expected, Helper.ParseBitrate(input));
    }

    [Theory]
    [InlineData("abc")]
    [InlineData("")]
    [InlineData("12kb")]
    public void ParseBitrate_RejectsInvalid(string input)
    {
        Assert.Throws<ArgumentException>(() => Helper.ParseBitrate(input));
    }

    [Fact]
    public void PresetLoader_ResolveExtendsAndOverride()
    {
        var raw = new Dictionary<string, Dictionary<string, object?>>(StringComparer.Ordinal)
        {
            ["_base"] = new() { ["format"] = ".mp4", ["type"] = "video", ["dimension"] = 3840L },
            ["child"] = new() { ["extends"] = "_base", ["dimension"] = 1920L },
        };
        var resolved = PresetLoader.ResolveExtends(raw, "child");
        Assert.Equal(".mp4", resolved["format"]);
        Assert.Equal(1920L, resolved["dimension"]); // 子级覆盖
        Assert.False(resolved.ContainsKey("extends"));

        // 循环继承
        var cyc = new Dictionary<string, Dictionary<string, object?>>(StringComparer.Ordinal)
        {
            ["a"] = new() { ["extends"] = "b" },
            ["b"] = new() { ["extends"] = "a" },
        };
        Assert.Throws<InvalidOperationException>(() => PresetLoader.ResolveExtends(cyc, "a"));
    }

    [Fact]
    public void PresetLoader_MergeRequiresOverride()
    {
        var baseP = new Dictionary<string, Dictionary<string, object?>>(StringComparer.Ordinal)
        {
            ["hevc_2k"] = new() { ["videoQuality"] = 23L },
        };
        var layer = new PresetLoader.Layer
        {
            Path = "user.yaml",
            Presets = new Dictionary<string, Dictionary<string, object?>>(StringComparer.Ordinal)
            {
                ["hevc_2k"] = new() { ["videoQuality"] = 99L }, // 无 _override → 跳过
                ["my_custom"] = new() { ["videoQuality"] = 1L }, // 新增 → 加入
            },
        };
        var merged = PresetLoader.MergePresets(baseP, layer);
        Assert.Equal(23L, merged["hevc_2k"]["videoQuality"]);
        Assert.True(merged.ContainsKey("my_custom"));

        var layer2 = new PresetLoader.Layer
        {
            Path = "user.yaml",
            Presets = new Dictionary<string, Dictionary<string, object?>>(StringComparer.Ordinal)
            {
                ["hevc_2k"] = new() { ["videoQuality"] = 99L, ["_override"] = true },
            },
        };
        var merged2 = PresetLoader.MergePresets(baseP, layer2);
        Assert.Equal(99L, merged2["hevc_2k"]["videoQuality"]);
    }

    [Fact]
    public void FFmpegPresets_InitLoadsBundledYaml()
    {
        FFmpegPresets.Init();
        Assert.Contains("hevc_2k", FFmpegPresets.GetAllNames());
        Assert.Contains("audio_extract", FFmpegPresets.GetAllNames());
        var p = FFmpegPresets.GetPreset("hevc_2k")!;
        Assert.Equal(".mp4", p.Format);
        Assert.Equal("video", p.Type);
        Assert.Equal("hevc", p.VideoCodecFamily);
        Assert.Equal(23, p.VideoQuality);
        Assert.Equal(8_000_000, p.MaxBitrate);
        Assert.Equal(192_000, p.AudioBitrate);
        Assert.Equal(1920, p.Dimension);
        Assert.True(p.SmartBitrate);
    }

    [Fact]
    public void FFmpegPresets_CreateFromArgvAliases()
    {
        FFmpegPresets.Init();
        var p = FFmpegPresets.CreateFromArgv(new FFmpegPresets.ArgvShim { Preset = "anime" });
        Assert.Equal("hevc_2k", p.Name);
        Assert.True(p.UserArgs.Anime);

        var p2 = FFmpegPresets.CreateFromArgv(new FFmpegPresets.ArgvShim { Preset = "h264" });
        Assert.Equal("h264_2k", p2.Name);

        var p3 = FFmpegPresets.CreateFromArgv(new FFmpegPresets.ArgvShim { VideoBitrate = 3000_000 });
        Assert.Equal(3_000_000, p3.UserArgs.VideoBitrate);

        var p4 = FFmpegPresets.CreateFromArgv(new FFmpegPresets.ArgvShim { VideoCodec = "copy" });
        Assert.True(p4.UserArgs.VideoCopy);
        Assert.Equal("", p4.Filters);
    }

    [Fact]
    public void HwDetect_ParseOutputs()
    {
        var encoders = HwDetect.ParseEncoders("""
             V....D libx264              libx264 H.264 / AVC
             V....D h264_nvenc           NVIDIA NVENC H.264 encoder
            """);
        Assert.Equal(["h264_nvenc", "libx264"], encoders.OrderBy(x => x));

        var hwaccels = HwDetect.ParseHwaccels("""
            Hardware acceleration methods:
            cuda
            d3d11va
            qsv
            """);
        Assert.Equal(3, hwaccels.Count);

        var filters = HwDetect.ParseFilters("""
             .. scale_cuda        V->V       GPU resizer
             T.. yadif            V->V       Deinterlace
            """);
        Assert.Equal(["scale_cuda", "yadif"], filters.OrderBy(x => x));
    }

    [Fact]
    public void HwDetect_ParseVersionInfo()
    {
        var (version, configuration) = HwDetect.ParseVersionInfo(
            "ffmpeg version N-126733-gfddc59cf3-2026-09-20-nonfree Copyright (c) 2000-2026 the FFmpeg developers\n" +
            "configuration: --enable-gpl --enable-nonfree\n");
        Assert.Equal("N-126733-gfddc59cf3-2026-09-20-nonfree", version);
        Assert.Equal("--enable-gpl --enable-nonfree", configuration);
    }

    [Fact]
    public void HwDetect_CandidateTiers_VendorChain()
    {
        var caps = new HardwareCaps
        {
            FFmpegPath = "ffmpeg",
            Vendor = "nvidia",
            Usable = new Dictionary<string, bool> { ["cuda"] = true, ["qsv"] = false, ["amf"] = false, ["d3d"] = true, ["cpu"] = true },
            StaticOk = new Dictionary<string, bool> { ["cuda"] = true, ["d3d"] = true, ["cpu"] = true },
            Hwaccels = ["cuda", "d3d11va", "qsv"],
            FilterSupport = new Dictionary<string, bool> { ["scale_cuda"] = true },
            Gpus = [new GpuInfo { Vendor = "nvidia", Model = "NVIDIA GeForce RTX 4070", Generation = 40, Primary = true }],
        };
        var tiers = HwDetect.CandidateTiers(caps);
        // N 卡机器：cuda → swdec（编码器存在性由 caps.Encoders 决定，此处为空 → 不入场）→ d3d → cpu
        Assert.Equal(["cuda", "d3d", "cpu"], tiers);

        // swdec：有硬件编码器时入场，位置在 d3d 之前
        // swdec：有硬件编码器时入场（重建 caps，Encoders 为 init-only）
        caps = new HardwareCaps { FFmpegPath = "ffmpeg", Vendor = "nvidia", Usable = caps.Usable, StaticOk = caps.StaticOk, Hwaccels = caps.Hwaccels, FilterSupport = caps.FilterSupport, Encoders = new HashSet<string>(StringComparer.Ordinal) { "h264_nvenc" }, Gpus = caps.Gpus };
        tiers = HwDetect.CandidateTiers(caps);
        Assert.Equal(["cuda", "swdec", "d3d", "cpu"], tiers);
    }

    [Fact]
    public void HwDetect_CandidateTiers_ExplicitWhitelist()
    {
        var caps = new HardwareCaps
        {
            FFmpegPath = "ffmpeg",
            Vendor = "any",
            Usable = new Dictionary<string, bool> { ["cuda"] = false, ["cpu"] = true, ["d3d"] = true },
            StaticOk = new Dictionary<string, bool> { ["cpu"] = true },
            Hwaccels = ["d3d11va"],
            Gpus = [],
        };
        // 显式指定不可用层：只剩 cpu（自动降级，不硬失败）
        Assert.Equal(["cpu"], HwDetect.CandidateTiers(caps, "auto", "cuda"));
        // gpu 模式：不可用直接抛错
        Assert.ThrowsAny<Exception>(() => HwDetect.CandidateTiers(caps, "gpu", "cuda"));
    }

    [Fact]
    public void Gpu_GenerationAndMatrix()
    {
        Assert.Equal(40, Gpu.NvidiaGenerationOf("NVIDIA GeForce RTX 4070"));
        Assert.Equal(50, Gpu.NvidiaGenerationOf("GeForce RTX 5080"));
        Assert.Equal(30, Gpu.NvidiaGenerationOf("RTX 2050")); // Ampere 硅片特例
        Assert.Equal(20, Gpu.NvidiaGenerationOf("GeForce GTX 1660"));
        Assert.Equal(10, Gpu.NvidiaGenerationOf("GeForce GTX 1080"));
        Assert.Null(Gpu.NvidiaGenerationOf("Intel(R) UHD Graphics 750"));

        // 40 系：H.264 4:2:2 10bit 硬解明确不支持 → 预筛拦截
        Assert.Equal("no", Gpu.NvdecSupportOf(40, "h264", "yuv422p10le", 10));
        // 40 系：H.264 420 8bit 支持
        Assert.Equal("yes", Gpu.NvdecSupportOf(40, "h264", "yuv420p", 8));
        // 10 系：HEVC 444 无硬解
        Assert.Equal("no", Gpu.NvdecSupportOf(10, "hevc", "yuv444p", 8));
        // unknown → 无数据，不预筛
        Assert.Null(Gpu.NvdecSupportOf(40, "ffv1", "bgr0", null));
    }

    [Fact]
    public void Gpu_NormalizeVendor_OrderMatters()
    {
        Assert.Equal("nvidia", Gpu.NormalizeVendor("NVIDIA Corporation"));
        Assert.Equal("intel", Gpu.NormalizeVendor("Intel Corporation")); // 含 "ati" 子串也不误判
        Assert.Equal("amd", Gpu.NormalizeVendor("Advanced Micro Devices, Inc."));
        Assert.Equal("amd", Gpu.NormalizeVendor("ATI Radeon"));
        Assert.Equal("other", Gpu.NormalizeVendor("Microsoft Basic Display Adapter"));
    }
}
