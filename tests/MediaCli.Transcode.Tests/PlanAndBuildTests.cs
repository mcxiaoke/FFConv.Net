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
                    StreamIndex = 0,
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

    /// <summary>
    /// 流复制必须整段跳过滤镜。回归 P0-2：ffmpeg 对「-c:v copy + -vf」会直接报
    /// "Filtering and streamcopy cannot be used together" 并失败，
    /// 因此 --video-copy 与缩放/变速/帧率/前后滤镜叠加时绝不能产出 -vf / -af。
    /// 旧实现只清空了 preset.Filters，未清 Dimension/Speed/Framerate，
    /// 于是这些组合会生成注定失败的 ffmpeg 命令（并伴随音画不同步）。
    /// </summary>
    [Theory]
    [InlineData(1920L, 0.0, 0.0, null)]      // 4K 源 → 2K 缩放
    [InlineData(1920L, 1.5, 0.0, null)]      // 变速
    [InlineData(1920L, 0.0, 24.0, null)]     // 改帧率
    [InlineData(1920L, 0.0, 0.0, "yadif")]   // 前置滤镜
    public void CreateFFmpegArgs_VideoCopyNeverEmitsFilters(
        long dimension, double speed, double framerate, string? preFilter)
    {
        var entry = MakeEntry(w: 3840, h: 2160);
        entry.Preset.UserArgs.VideoCopy = true;
        entry.Preset.UserArgs.VideoCodec = "copy";
        entry.Preset.Filters = "";
        if (dimension > 0) entry.Preset.UserArgs.Dimension = dimension;
        if (speed > 0) entry.Preset.UserArgs.Speed = speed;
        if (framerate > 0) entry.Preset.UserArgs.Framerate = framerate;
        if (preFilter is not null) entry.Preset.PreFilters = preFilter;

        entry.DstArgs = FfmpegPlan.CalculateDstArgs(entry);
        var cpu = new HwPlan { Tier = HwAccel.Tiers.First(t => t.Name == "cpu") };
        var (args, _) = FfmpegBuild.CreateFFmpegArgs(entry, cpu);
        var middle = args[1].ToList();

        Assert.DoesNotContain("-vf", middle);
        Assert.DoesNotContain("-af", middle);   // 音频也不得变速，否则音画不同步
        Assert.Contains("-c:v", middle);
        Assert.Equal("copy", middle[middle.IndexOf("-c:v") + 1]);
    }

    /// <summary>非流复制场景仍应正常产出滤镜（确认上面的短路没有越界）。</summary>
    [Fact]
    public void CreateFFmpegArgs_NonCopyStillEmitsScaleFilter()
    {
        var entry = MakeEntry(w: 3840, h: 2160);
        entry.DstArgs = FfmpegPlan.CalculateDstArgs(entry);
        var cpu = new HwPlan { Tier = HwAccel.Tiers.First(t => t.Name == "cpu") };
        var (args, _) = FfmpegBuild.CreateFFmpegArgs(entry, cpu);
        var middle = args[1].ToList();
        Assert.Contains("-vf", middle);
        Assert.Contains(middle, a => a.Contains("scale=", StringComparison.Ordinal));
    }

    // ------------------------------------------------------------------
    // 同基名多容器文件的命名区分（{srcExt} 系列变量）
    // ------------------------------------------------------------------

    /// <summary>
    /// 回归：同一基名、不同容器的文件在默认后缀 `_{preset}` 下会映射到同一个目标名，
    /// 只有第一个能产出。{srcExt} 系列变量让用户可以稳定区分它们。
    ///
    /// 关键约束：{srcExt} 必须含点（可直接拼进文件名），{srcExtBare} 不含点。
    /// </summary>
    [Theory]
    [InlineData("Movie.mkv", "_av1{srcExt}", "Movie_av1.mkv")]
    [InlineData("Movie.mp4", "_av1{srcExt}", "Movie_av1.mp4")]
    [InlineData("Movie.webm", "_av1{srcExt}", "Movie_av1.webm")]
    [InlineData("Movie.mkv", "_{preset}_{srcExtBare}", "Movie_hevc_2k_mkv")]
    // 注意：基名（srcBase）始终居中拼接，{srcStem} 只是"可再引用一次源文件名"，
    // 不会替代它，因此结果为 Movie + Movie-out。
    [InlineData("Movie.mkv", "-{srcStem}-out", "Movie-Movie-out")]
    [InlineData("Movie.mkv", "-{srcName}", "Movie-Movie.mkv")]
    public void CreateDstBaseName_SourceDerivedVariables(string fileName, string suffixTemplate, string expectedBase)
    {
        var entry = MakeEntry(path: fileName);
        entry.Name = fileName;
        entry.Preset.Suffix = suffixTemplate;
        entry.Preset.Prefix = "";

        var (baseName, _, _) = FfmpegPlan.CreateDstBaseName(entry);

        Assert.Equal(expectedBase, baseName);
    }

    /// <summary>
    /// 用 {srcExt} 区分后，同基名多容器文件必须得到互不相同的目标名
    /// （这正是修复前会互相撞名、被判 destination_exists 的场景）。
    /// </summary>
    [Fact]
    public void CreateDstBaseName_SameStemDifferentContainers_ProduceDistinctNames()
    {
        var names = new List<string>();
        foreach (var ext in new[] { ".mkv", ".mp4", ".webm" })
        {
            var fileName = "Movie" + ext;
            var entry = MakeEntry(path: fileName);
            entry.Name = fileName;
            entry.Preset.Suffix = "_{preset}{srcExt}";
            entry.Preset.Prefix = "";
            var (baseName, _, _) = FfmpegPlan.CreateDstBaseName(entry);
            names.Add(baseName);
        }

        Assert.Equal(3, names.Distinct(StringComparer.OrdinalIgnoreCase).Count());
        Assert.Equal(["Movie_hevc_2k.mkv", "Movie_hevc_2k.mp4", "Movie_hevc_2k.webm"], names);
    }

    /// <summary>未使用新变量时保持原行为（默认后缀输出名不变，向后兼容）。</summary>
    [Fact]
    public void CreateDstBaseName_DefaultSuffixUnchanged()
    {
        var entry = MakeEntry(path: "Movie.mkv");
        entry.Name = "Movie.mkv";
        entry.Preset.Suffix = "_{preset}";
        entry.Preset.Prefix = "";
        var (baseName, _, _) = FfmpegPlan.CreateDstBaseName(entry);
        Assert.Equal("Movie_hevc_2k", baseName);
    }

    // ------------------------------------------------------------------
    // 批内目标冲突的识别
    // ------------------------------------------------------------------

    /// <summary>
    /// 回归：跳过原因必须区分「输出目录里的旧文件」与「本批次自己刚产出的文件」。
    /// 前者要清旧文件/勾选覆盖，后者要改命名模板——两者动作完全不同。
    /// </summary>
    [Fact]
    public void RefineDestinationConflict_MarksBatchConflictAndRecordsBlocker()
    {
        var entry = new TranscodeEntry { Path = @"C:\in\Movie.mp4", Name = "Movie.mp4" };
        entry.DstExists = true;
        entry.DstExistsPath = @"C:\out\Movie_av1_2k.mp4";
        var batchOutputs = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            [@"C:\out\Movie_av1_2k.mp4"] = @"C:\in\Movie.mkv",
        };

        FfmpegTask.RefineDestinationConflict(entry, batchOutputs);

        Assert.Equal(SkipReason.DestinationConflictInBatch, entry.SkipReason);
        Assert.Equal(@"C:\in\Movie.mkv", entry.ConflictedWithSource);
    }

    /// <summary>磁盘上的旧文件（不在本批产出表里）必须保持 destination_exists。</summary>
    [Fact]
    public void RefineDestinationConflict_OldFileOnDisk_StaysDestinationExists()
    {
        var entry = new TranscodeEntry { Path = @"C:\in\Movie.mp4", Name = "Movie.mp4" };
        entry.DstExists = true;
        entry.DstExistsPath = @"C:\out\Movie_av1_2k.mp4";

        FfmpegTask.RefineDestinationConflict(entry, new Dictionary<string, string>());

        Assert.Equal(SkipReason.DestinationExists, entry.SkipReason);
        Assert.Null(entry.ConflictedWithSource);
    }

    /// <summary>路径大小写不同不应漏判（Windows 路径不区分大小写，会互相覆盖）。</summary>
    [Fact]
    public void RefineDestinationConflict_IsCaseInsensitive()
    {
        var entry = new TranscodeEntry { Path = @"C:\in\Movie.mp4", Name = "Movie.mp4" };
        entry.DstExists = true;
        entry.DstExistsPath = @"C:\OUT\Movie_AV1_2K.MP4";
        var batchOutputs = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            [@"c:\out\movie_av1_2k.mp4"] = @"C:\in\Movie.mkv",
        };

        FfmpegTask.RefineDestinationConflict(entry, batchOutputs);

        Assert.Equal(SkipReason.DestinationConflictInBatch, entry.SkipReason);
    }

    /// <summary>同一文件被重复扫描到时，不该把自己报成冲突方。</summary>
    [Fact]
    public void RefineDestinationConflict_DoesNotSelfReport()
    {
        var entry = new TranscodeEntry { Path = @"C:\in\Movie.mp4", Name = "Movie.mp4" };
        entry.DstExists = true;
        entry.DstExistsPath = @"C:\out\Movie_av1_2k.mp4";
        var batchOutputs = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            [@"C:\out\Movie_av1_2k.mp4"] = @"C:\in\Movie.mp4",
        };

        FfmpegTask.RefineDestinationConflict(entry, batchOutputs);

        Assert.Equal(SkipReason.DestinationExists, entry.SkipReason);
    }

    /// <summary>非目标冲突类跳过原因不得被改写（只在 destination_exists 上细化）。</summary>
    [Fact]
    public void RefineDestinationConflict_LeavesOtherSkipReasonsAlone()
    {
        var entry = new TranscodeEntry { Path = @"C:\in\Movie.mp4", Name = "Movie.mp4" };
        entry.Skipped = true;
        entry.SkipReason = SkipReason.ShortDuration;
        entry.DstExistsPath = @"C:\out\Movie_av1_2k.mp4";
        var batchOutputs = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            [@"C:\out\Movie_av1_2k.mp4"] = @"C:\in\Movie.mkv",
        };

        FfmpegTask.RefineDestinationConflict(entry, batchOutputs);

        Assert.Equal(SkipReason.ShortDuration, entry.SkipReason);
    }


    /// <summary>
    /// 回归 P1-3：BuildStreamArgs 依赖 RxWhitespace / RxMapMetadata 做清洗。
    /// 这两处的 verbatim 正则曾被写成 <c>\\s</c>（匹配字面反斜杠），导致清洗静默失效。
    /// </summary>
    [Fact]
    public void CreateFFmpegArgs_StreamArgsAreCleaned()
    {
        var entry = MakeEntry(w: 1920, h: 1080);
        entry.Preset.StreamArgs = "-map_metadata:s:v BPS=   -map_metadata 0";
        entry.DstArgs = FfmpegPlan.CalculateDstArgs(entry);
        var cpu = new HwPlan { Tier = HwAccel.Tiers.First(t => t.Name == "cpu") };
        var (args, _) = FfmpegBuild.CreateFFmpegArgs(entry, cpu);
        var flat = string.Join(" ", args.SelectMany(a => a));

        Assert.DoesNotContain("map_metadata:s:v", flat);      // 清洗生效
        Assert.Contains("-map_metadata", flat);
        Assert.DoesNotContain("   ", flat);                    // 空白压缩生效
    }

    /// <summary>
    /// 回归 P1-3：带 -c:a 前缀的音频参数串在降级时不能丢掉 " -c:a" 前缀。
    /// 旧正则失效时 "-c:a libopus" 会被当成整串去查编码器表，最终退化成裸 "aac"。
    /// </summary>
    [Fact]
    public void FallbackAudioEncoder_PreservesCodecFlagPrefix()
    {
        var encoders = new HashSet<string>(StringComparer.Ordinal) { "aac", "libopus" };
        Assert.Equal("-c:a libopus", FfmpegBuild.FallbackAudioEncoder("-c:a libopus", encoders));
        Assert.Equal("-c:a aac", FfmpegBuild.FallbackAudioEncoder("-c:a libfdk_aac", encoders));
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

    [Fact]
    public void CreateFFmpegArgs_CoverVideoStream_MapsCorrectIndex()
    {
        var entry = MakeEntry("movie.mkv");
        entry.Info!.Video!.StreamIndex = 1; // Stream 0 was attached cover
        entry.DstArgs = FfmpegPlan.CalculateDstArgs(entry);
        var cpu = new HwPlan { Tier = HwAccel.Tiers.First(t => t.Name == "cpu") };
        var (args, _) = FfmpegBuild.CreateFFmpegArgs(entry, cpu);
        var input = args[0].ToList();
        var mapIdx = input.IndexOf("-map");
        Assert.True(mapIdx >= 0);
        Assert.Equal("0:1", input[mapIdx + 1]);
    }

    [Fact]
    public void CreateFFmpegArgs_NoStreamIndex_DropsMap()
    {
        var entry = MakeEntry("movie.mkv");
        entry.Info!.Video!.StreamIndex = null;
        entry.DstArgs = FfmpegPlan.CalculateDstArgs(entry);
        var cpu = new HwPlan { Tier = HwAccel.Tiers.First(t => t.Name == "cpu") };
        var (args, _) = FfmpegBuild.CreateFFmpegArgs(entry, cpu);
        var input = args[0].ToList();
        Assert.DoesNotContain("-map", input);
    }

    [Fact]
    public void CreateFFmpegArgs_Webm_DropsSubtitles()
    {
        var entry = MakeEntry("movie.webm");
        entry.Preset.Format = ".webm";
        entry.DstArgs = FfmpegPlan.CalculateDstArgs(entry);
        var cpu = new HwPlan { Tier = HwAccel.Tiers.First(t => t.Name == "cpu") };
        var (args, _) = FfmpegBuild.CreateFFmpegArgs(entry, cpu);
        var input = args[0].ToList();
        Assert.Contains("-sn", input);
        Assert.DoesNotContain("-c:s", input);
    }

    [Fact]
    public void CreateFFmpegArgs_SameResolution_SkipsScaleFilter()
    {
        var entry = MakeEntry("movie.mp4", w: 1920, h: 1080);
        entry.Preset = FFmpegPresets.GetPreset("hevc_2k")!.Clone();
        entry.Preset.PreFilters = null;
        entry.Preset.PostFilters = null;
        entry.DstArgs = FfmpegPlan.CalculateDstArgs(entry);
        var cpu = new HwPlan { Tier = HwAccel.Tiers.First(t => t.Name == "cpu") };
        var (args, _) = FfmpegBuild.CreateFFmpegArgs(entry, cpu);
        var middle = args[1].ToList();
        Assert.DoesNotContain(middle, a => a.Contains("scale=", StringComparison.Ordinal));
    }

    [Fact]
    public void CreateFFmpegArgs_AudioCopy_IncompatibleContainerFallback()
    {
        var entry = MakeEntry("movie.mkv");
        entry.Preset.Format = ".mp4";
        entry.Preset.AudioCodec = "copy";
        entry.Info!.Audio!.Format = "vorbis"; // MP4 不支持直接 copy Vorbis
        entry.DstArgs = FfmpegPlan.CalculateDstArgs(entry);
        var cpu = new HwPlan { Tier = HwAccel.Tiers.First(t => t.Name == "cpu") };
        var (args, _) = FfmpegBuild.CreateFFmpegArgs(entry, cpu);
        var middle = args[1].ToList();
        var aIdx = middle.IndexOf("-c:a");
        Assert.True(aIdx >= 0);
        Assert.Equal("aac", middle[aIdx + 1]);
    }
}
