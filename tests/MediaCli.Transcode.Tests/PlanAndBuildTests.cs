using MediaCli.Transcode.Build;
using MediaCli.Transcode.Presets;
using MediaCli.Transcode.Run;
using MediaCli.Transcode.Hardware;
using MediaCli.Transcode.Model;
using MediaCli.Transcode.Planning;
using MediaCli.Transcode.Support;
using Xunit;

namespace MediaCli.Transcode.Tests;

/// <summary>ffmpeg_plan.js / ffmpeg_build.js 核心逻辑的等价性测试。</summary>
public class PlanAndBuildTests
{
    static PlanAndBuildTests() => FFmpegPresets.Init();
    private static TranscodeEntry MakeEntry(
        string path = "video.mp4",
        int w = 3840, int h = 2160, long srcVideoBitrate = 10_000_000, long srcAudioBitrate = 192_000)
    {
        return new TranscodeEntry
        {
            Path = path,
            Name = Path.GetFileName(path),
            Size = 100 * 1000 * 1000,
            Info = new MediaInfo
            {
                Duration = 60,
                Bitrate = srcVideoBitrate + srcAudioBitrate + 48_000,
                Video = new VideoInfo
                {
                    Format = "hevc", Width = w, Height = h, Bitrate = srcVideoBitrate,
                    FrameRate = 23.976, PixelFormat = "yuv420p10le", BitDepth = 10,
                },
                Audio = new AudioInfo { Format = "aac", Bitrate = srcAudioBitrate },
            },
            Preset = FFmpegPresets.GetPreset("hevc_2k")!.Clone(),
        };
    }

    [Fact]
    public void CalculateDstArgs_SmartAudioBitrate()
    {
        // 音频源 320k → smartBitrate 320k；minNoZero(320k,320k)=320k
        var entry2 = new TranscodeEntry
        {
            Path = "song.m4a",
            Name = "song.m4a",
            Info = new MediaInfo
            {
                Duration = 100,
                Bitrate = 320_000,
                Audio = new AudioInfo { Format = "aac", Bitrate = 320_000 },
            },
            Preset = FFmpegPresets.GetPreset("aac_medium")!.Clone(),
        };
        var dst = FfmpegPlan.CalculateDstArgs(entry2);
        Assert.Equal(256_000, dst.DstAudioBitrate); // smartBitrate: 320k 源 → 下一档 256k（320k 不严格大于阈值）
    }

    [Fact]
    public void CalculateDstArgs_ResolutionPowerScale()
    {
        // 4K 预设（8M）跑 1080p 源：scale=(实际/锚点)^0.75 ≈ 0.3536 → 8M×0.3536 ≈ 2.83M
        var entry = MakeEntry(w: 1920, h: 1080, srcVideoBitrate: 100_000_000);
        entry.Preset = FFmpegPresets.GetPreset("hevc_4k")!.Clone(); // 4K 标准 maxBitrate 14M → videoBitrate=0
        entry.Preset.VideoBitrate = 8_000_000; // 模拟指定视频码率
        var dst = FfmpegPlan.CalculateDstArgs(entry);
        var expected = (long)Math.Round(8_000_000 * Math.Pow((1920.0 * 1080) / (1920.0 * 1080), 0.75));
        // 锚点 = 源按 3840 等比缩放（禁止放大不影响锚点）：3840x2160 理论像素
        // anchorRatio = 3840/1920 = 2 → anchor = 3840*2160
        expected = (long)Math.Round(8_000_000 * Math.Pow((1920.0 * 1080) / (3840.0 * 2160), 0.75));
        Assert.Equal(expected, dst.DstVideoBitrate);
        Assert.Equal(1920, dst.DstWidth);
        Assert.Equal(1080, dst.DstHeight);
    }

    [Fact]
    public void CalculateDstArgs_AnimeQualityAdjust()
    {
        var entry = MakeEntry(w: 1920, h: 1080);
        entry.Preset = FFmpegPresets.GetPreset("av1_2k")!.Clone(); // q=33
        entry.Preset.UserArgs.Anime = true;
        var dst = FfmpegPlan.CalculateDstArgs(entry);
        Assert.Equal(37, dst.DstVideoQuality); // av1 +4

        entry.Preset = FFmpegPresets.GetPreset("hevc_2k")!.Clone(); // q=23
        entry.Preset.UserArgs.Anime = true;
        dst = FfmpegPlan.CalculateDstArgs(entry);
        Assert.Equal(25, dst.DstVideoQuality); // hevc +2
    }

    [Fact]
    public void CalculateDstArgs_FrameRateTolerance()
    {
        var entry = MakeEntry(w: 1920, h: 1080);
        entry.Preset.Framerate = 23.976; // 源 23.976：容差内不重采样
        var dst = FfmpegPlan.CalculateDstArgs(entry);
        Assert.Equal(0, dst.DstFrameRate);

        // 60fps 源 + 25 目标：真实降帧
        Assert.NotNull(entry.Info?.Video);
        entry.Info.Video.FrameRate = 60;
        entry.Preset.Framerate = 25;
        dst = FfmpegPlan.CalculateDstArgs(entry);
        Assert.Equal(25, dst.DstFrameRate);
    }

    [Fact]
    public void SelectPreferredSubtitle_ChineseToken()
    {
        var subs = new[] { "movie.chs.ass", "movie.eng.srt" };
        Assert.Equal("movie.chs.ass", FfmpegPlan.SelectPreferredSubtitle(subs));
        var subs2 = new[] { "movie.eng.srt", "movie.zh-CN.srt" };
        Assert.Equal("movie.zh-CN.srt", FfmpegPlan.SelectPreferredSubtitle(subs2));
        var subs3 = new[] { "movie.eng.srt", "movie.jp.ass" };
        Assert.Equal("movie.eng.srt", FfmpegPlan.SelectPreferredSubtitle(subs3)); // 无中文回退第一个
    }

    [Fact]
    public void SplitPresetFilterSegments_LegacyPlaceholder()
    {
        var preset = new FFmpegPreset { Filters = "yadif=1,{scaleFilter},unsharp=3:3" };
        var temp = new TempPreset(preset, new DstArgs());
        var seg = FfmpegBuild.SplitPresetFilterSegments(temp);
        Assert.Equal("yadif=1", seg.Pre);
        Assert.Equal("unsharp=3:3", seg.Post);
        Assert.True(seg.ScaleRequested);
    }

    [Fact]
    public void FallbackAudioEncoder_StaticDowngrade()
    {
        var encoders = new HashSet<string>(StringComparer.Ordinal) { "aac" };
        Assert.Equal("aac", FfmpegBuild.FallbackAudioEncoder("libfdk_aac", encoders, strict: false));
        Assert.Equal("libfdk_aac", FfmpegBuild.FallbackAudioEncoder("libfdk_aac", encoders, strict: true));
        Assert.Equal("copy", FfmpegBuild.FallbackAudioEncoder("copy", encoders));
        Assert.Equal("libfdk_aac", FfmpegBuild.FallbackAudioEncoder("libfdk_aac", null)); // 未知集合原样
    }

    [Fact]
    public void CreateFFmpegArgs_VideoCopy()
    {
        var entry = MakeEntry(w: 1920, h: 1080);
        entry.Preset.UserArgs.VideoCodec = "copy";
        entry.DstArgs = FfmpegPlan.CalculateDstArgs(entry);
        var cpu = new HwPlan { Tier = HwAccel.Tiers.First(t => t.Name == "cpu") };
        var (args, _) = FfmpegBuild.CreateFFmpegArgs(entry, cpu);
        Assert.Contains("-c:v", args[1]);
        Assert.Equal("copy", args[1][args[1].ToList().IndexOf("-c:v") + 1]);
    }

    [Fact]
    public void CreateFFmpegArgs_CqModeWithMaxrate()
    {
        var entry = MakeEntry(w: 1920, h: 1080);
        entry.Preset.UserArgs.VideoCodec = "libx265";
        entry.Preset.UserArgs.VideoQuality = 24;
        entry.DstArgs = FfmpegPlan.CalculateDstArgs(entry);
        var cpu = new HwPlan { Tier = HwAccel.Tiers.First(t => t.Name == "cpu") };
        var (args, _) = FfmpegBuild.CreateFFmpegArgs(entry, cpu);
        var middle = args[1];
        Assert.Equal("-c:v", middle[middle.ToList().IndexOf("-c:v")]);
        Assert.Equal("libx265", middle[middle.ToList().IndexOf("-c:v") + 1]);
        // 显式 maxBitrate：CQ 模式追加 -maxrate/-bufsize（hevc ×1.5 缺省走 maxBitrate 字段）
        Assert.Contains("-maxrate", middle);
    }

    [Fact]
    public void CreateFFmpegArgs_MkvSubtitleCopy()
    {
        var entry = MakeEntry("movie.mkv");
        entry.DstArgs = FfmpegPlan.CalculateDstArgs(entry);
        entry.Preset.Format = ".mkv";
        entry.FileDstTemp = "out_tmp@x@tmp_.mkv";
        var cpu = new HwPlan { Tier = HwAccel.Tiers.First(t => t.Name == "cpu") };
        var (args, _) = FfmpegBuild.CreateFFmpegArgs(entry, cpu);
        var input = args[0];
        var idx = input.ToList().IndexOf("-c:s");
        Assert.Equal("copy", input[idx + 1]); // MKV 容器字幕无损复制
        Assert.Contains("0:s?", input);
    }

    [Fact]
    public void CreateDstBaseName_Template()
    {
        var entry = MakeEntry("movie.mkv");
        entry.DstArgs = FfmpegPlan.CalculateDstArgs(entry);
        var (baseName, prefix, suffix) = FfmpegPlan.CreateDstBaseName(entry);
        Assert.Equal("[SHANA] ", prefix);
        Assert.Equal("_hevc_2k", suffix);
        Assert.Equal("[SHANA] movie_hevc_2k", baseName);
    }

    [Fact]
    public void MinNoZero_HandlesAllZero()
    {
        Assert.Equal(0, FfmpegPlan.MinNoZero(0, 0));
        Assert.Equal(48_000, FfmpegPlan.MinNoZero(48_000, 0));
        Assert.Equal(96_000, FfmpegPlan.MinNoZero(96_000, 192_000));
    }

    [Fact]
    public void ExtractFFmpegError_FindsFirstErrorLine()
    {
        var stderr = """
            [info] Input #0, matroska,webm, from 'x.mkv'
            [error] Impossible to convert between the formats
            [error] Link 'x' -> 'y'
            [error]     dst: cuda
            [info] Conversion failed!
            """;
        var msg = FfmpegRun.ExtractFFmpegError(stderr);
        Assert.Equal("Impossible to convert between the formats", msg);
    }

    [Fact]
    public void ExtractFFmpegError_FeatureWordFallback()
    {
        // JS 特征词正则命中 "Error/failed" 等词；无 [error] 标记时取第一条特征词行
        var msg = FfmpegRun.ExtractFFmpegError("Error while opening encoder for output stream #0:0\nConversion failed!");
        Assert.Equal("Error while opening encoder for output stream #0:0", msg);
    }
}
