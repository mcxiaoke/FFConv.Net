using MediaCli.Transcode.Hardware;
using MediaCli.Transcode.Model;
using Xunit;

namespace MediaCli.Transcode.Tests;

/// <summary>hwaccel.js 核心纯函数的等价性测试。</summary>
public class HwAccelTests
{
    [Theory]
    [InlineData(607.5, 608)]   // ffmpeg scale 用四舍五入：Math.round(607.5)=608 → 608
    [InlineData(569.53, 570)]  // Math.round(569.53)=570 → 570
    public void ToEven_RoundsLikeFfmpeg(double input, int expected)
    {
        // 与 JS toEven 逐值对齐（已用 node 跑 JS 原版核实：608 / 570）
        Assert.Equal(expected, HwAccel.ToEven(input));
    }

    [Fact]
    public void ToEven_Exact()
    {
        Assert.Equal(608, HwAccel.ToEven(607.5));
        Assert.Equal(570, HwAccel.ToEven(569.53));
        Assert.Equal(1080, HwAccel.ToEven(1080));
        Assert.Equal(606, HwAccel.ToEven(607.4));
    }

    [Fact]
    public void CalcLongEdge_NoUpscale_EvenAlign()
    {
        // 4K 源压 1080 预设：4096x2160 → 1080x570（长边 4096 → target 1080）
        var s = HwAccel.CalcLongEdge(4096, 2160, 1080);
        Assert.Equal(1080, s.W);
        Assert.Equal(570, s.H);

        // 小源跑大预设：禁止放大
        var s2 = HwAccel.CalcLongEdge(1280, 720, 1920);
        Assert.Equal(1280, s2.W);
        Assert.Equal(720, s2.H);

        // 竖屏
        var s3 = HwAccel.CalcLongEdge(1080, 1920, 1280);
        Assert.Equal(720, s3.W);
        Assert.Equal(1280, s3.H);
    }

    [Theory]
    [InlineData("yuv420p10le", "10bit")]
    [InlineData("yuv420p", "8bit")]
    [InlineData("YUV4:2:0", "8bit")] // mediainfo 形态：位深不在字符串里
    public void BitDepthOf_ParsesPixFmt(string pixFmt, string expected)
    {
        Assert.Equal(expected, HwAccel.BitDepthOf(pixFmt, null));
    }

    [Fact]
    public void BitDepthOf_ExplicitWins()
    {
        Assert.Equal("10bit", HwAccel.BitDepthOf("YUV4:2:0", 10));
        Assert.Equal("8bit", HwAccel.BitDepthOf("yuv420p10le", 8));
    }

    [Fact]
    public void ValidateSpeed()
    {
        Assert.Equal(1, HwAccel.ValidateSpeed(null));
        Assert.Equal(1, HwAccel.ValidateSpeed(0));
        Assert.Equal(1, HwAccel.ValidateSpeed(1));
        Assert.Equal(1.5, HwAccel.ValidateSpeed(1.5));
        Assert.Throws<ArgumentException>(() => HwAccel.ValidateSpeed(3.0));
        Assert.Throws<ArgumentException>(() => HwAccel.ValidateSpeed(0.2));
    }

    [Theory]
    [InlineData("hevc_nvenc", "hevc")]
    [InlineData("libx265", "hevc")]
    [InlineData("h264_qsv", "h264")]
    [InlineData("av1_qsv", "av1")]
    [InlineData("libvpx-vp9", "vp9")]
    [InlineData("av01", "av1")]
    public void CodecFamilyOf(string encoder, string expected)
    {
        Assert.Equal(expected, HwAccel.CodecFamilyOf(encoder));
    }

    [Fact]
    public void CodecFamilyOfPreset_Priority()
    {
        var preset = new FFmpegPreset { VideoCodecFamily = "hevc" };
        Assert.Equal("hevc", HwAccel.CodecFamilyOfPreset(preset));
        preset.UserArgs.VideoCodec = "h264_nvenc"; // 显式编码器优先
        Assert.Equal("h264", HwAccel.CodecFamilyOfPreset(preset));
        Assert.Equal("h264", HwAccel.CodecFamilyOfPreset(null));
    }

    [Fact]
    public void ScaleFormatOverride_10bitH264Only()
    {
        var cuda = HwAccel.Tiers.First(t => t.Name == "cuda");
        Assert.Equal("nv12", HwAccel.ScaleFormatOverride(cuda, "h264", "yuv420p10le", 10));
        Assert.Null(HwAccel.ScaleFormatOverride(cuda, "hevc", "yuv420p10le", 10));
        Assert.Null(HwAccel.ScaleFormatOverride(cuda, "h264", "yuv420p", 8));
        var qsv = HwAccel.Tiers.First(t => t.Name == "qsv");
        Assert.Equal("nv12", HwAccel.ScaleFormatOverride(qsv, "h264", "yuv422p10le", null));
        var cpu = HwAccel.Tiers.First(t => t.Name == "cpu");
        Assert.Null(HwAccel.ScaleFormatOverride(cpu, "h264", "yuv420p10le", 10));
    }

    [Fact]
    public void NeedsDepthAlign_UnknownIsConservative()
    {
        Assert.True(HwAccel.NeedsDepthAlign("YUV4:2:0", null));  // unknown → 需要对齐
        Assert.True(HwAccel.NeedsDepthAlign("yuv420p10le", null));
        Assert.False(HwAccel.NeedsDepthAlign("yuv420p", null));
        Assert.False(HwAccel.NeedsDepthAlign("yuv420p10le", 8)); // 显式 8bit 覆盖
    }

    [Fact]
    public void BuildScaleFilter_CudaWithTrailingFormat()
    {
        var cuda = HwAccel.Tiers.First(t => t.Name == "cuda");
        var filter = HwAccel.BuildScaleFilter(cuda, new Size(1920, 1080), null);
        Assert.Equal("scale_cuda=w=1920:h=1080:interp_algo=lanczos,format=cuda", filter);
        var withFormat = HwAccel.BuildScaleFilter(cuda, new Size(1920, 1080), "nv12");
        Assert.Equal("scale_cuda=w=1920:h=1080:interp_algo=lanczos:format=nv12,format=cuda", withFormat);
        // 只做格式对齐、不做缩放（size=null）
        var noScale = HwAccel.BuildScaleFilter(cuda, null, "nv12");
        Assert.Equal("scale_cuda=interp_algo=lanczos:format=nv12,format=cuda", noScale);
    }

    [Fact]
    public void BuildVideoFilters_HwDomainChain()
    {
        var cuda = HwAccel.Tiers.First(t => t.Name == "cuda");
        // 无用户滤镜：pre → setpts → scale_cuda → fps
        var chain = HwAccel.BuildVideoFilters(new HwVideoFilterOptions
        {
            Tier = cuda, Size = new Size(1920, 1080), Speed = 1.5, Framerate = 25,
        });
        Assert.Equal("setpts=PTS/1.5,scale_cuda=w=1920:h=1080:interp_algo=lanczos:format=nv12,format=cuda,fps=25", chain);
        // 有用户滤镜（显存帧域）：先 hwdownload
        var swDomain = HwAccel.BuildVideoFilters(new HwVideoFilterOptions
        {
            Tier = cuda, Size = new Size(1920, 1080), PreFilters = "yadif=1", PostFilters = "unsharp=3:3",
        });
        Assert.StartsWith("hwdownload,format=nv12,yadif=1,", swDomain);
        Assert.EndsWith(",scale=w=1920:h=1080:flags=lanczos,unsharp=3:3", swDomain);
    }

    [Fact]
    public void BuildEncoderArgs_NvencCqAndVbr()
    {
        var nvenc = HwAccel.BuildEncoderArgs("cuda", new EncoderArgs { Quality = 23, CodecFamily = "hevc" });
        // hw-hevc VMAF 偏移 +5 → 23+5=28
        var expected = new[] { "-c:v", "hevc_nvenc", "-rc", "vbr", "-tune", "hq", "-rc-lookahead", "30", "-cq", "28", "-b:v", "0" };
        Assert.Equal(expected, nvenc);

        var vbr = HwAccel.BuildEncoderArgs("cuda", new EncoderArgs
        {
            Quality = 23, CodecFamily = "hevc", Bitrate = 4_000_000,
        });
        // maxBitrate 缺省 = bitrate × 1.5 = 6000K
        Assert.Contains("-b:v", vbr);
        Assert.Equal("4000K", vbr[Array.IndexOf(vbr, "-b:v") + 1]);
        Assert.Equal("6000K", vbr[Array.IndexOf(vbr, "-maxrate") + 1]);
    }

    [Fact]
    public void BuildEncoderArgs_CpuAnimeHevc()
    {
        var args = HwAccel.BuildEncoderArgs("cpu", new EncoderArgs
        {
            Quality = 23, CodecFamily = "hevc", Anime = true, Encoders = ["libx265"],
        });
        Assert.Contains("-x265-params", args);
        Assert.Contains("no-sao=1:aq-mode=3", args);
        // cpu hevc 无 VMAF 偏移
        Assert.Equal("23", args[Array.IndexOf(args, "-crf") + 1]);
    }

    [Fact]
    public void BuildEncoderArgs_QsvCq()
    {
        var args = HwAccel.BuildEncoderArgs("qsv", new EncoderArgs { Quality = 24, CodecFamily = "h264" });
        // hw-h264 +7 → 31
        var expected = new[] { "-c:v", "h264_qsv", "-global_quality", "31", "-b:v", "0" };
        Assert.Equal(expected, args);
    }

    [Fact]
    public void BuildEncoderArgs_SwdecDepthAlign()
    {
        var nvidiaRow = new Dictionary<string, string>
        {
            ["h264"] = "h264_nvenc", ["hevc"] = "hevc_nvenc", ["av1"] = "av1_nvenc",
        };
        var swdec = new TierDef
        {
            Name = "swdec", Vendor = "any", Filter = "scale", FilterArgs = "flags=lanczos",
            RequiresFilter = true, EncoderRow = nvidiaRow,
        };
        var args = HwAccel.BuildEncoderArgs("swdec", new EncoderArgs
        {
            Quality = 23, CodecFamily = "h264", PixFmt = "yuv420p10le", BitDepth = 10, Tier = swdec,
        });
        Assert.Contains("-pix_fmt", args);
        Assert.Equal("yuv420p", args[Array.IndexOf(args, "-pix_fmt") + 1]);
        // hevc 不需要对齐
        var hevcArgs = HwAccel.BuildEncoderArgs("swdec", new EncoderArgs
        {
            Quality = 23, CodecFamily = "hevc", PixFmt = "yuv420p10le", BitDepth = 10, Tier = swdec,
        });
        Assert.DoesNotContain("-pix_fmt", hevcArgs);
    }

    [Fact]
    public void BuildEncoderArgs_RuntimeFallback()
    {
        // 本机只有 librav1e 时：av1 从 libsvtav1 回退
        var args = HwAccel.BuildEncoderArgs("cpu", new EncoderArgs
        {
            Quality = 30, CodecFamily = "av1", Encoders = ["librav1e", "libx264"],
        });
        Assert.Equal("librav1e", args[1]);
    }

    [Fact]
    public void QualityOffsetOf_Ladder()
    {
        Assert.Equal(6, HwAccel.QualityOffsetOf("cuda", "hevc", 20));
        Assert.Equal(6, HwAccel.QualityOffsetOf("cuda", "hevc", 30));
        Assert.Equal(1, HwAccel.QualityOffsetOf("cuda", "hevc", 38));
        Assert.Equal(5, HwAccel.QualityOffsetOf("cuda", "h264", 20));
        Assert.Equal(0, HwAccel.QualityOffsetOf("cpu", "h264", 20)); // x264 基准
    }

    [Fact]
    public void ProbeCacheKey_IncludesBitDepthAndSpeed()
    {
        var k1 = HwAccel.ProbeCacheKey("cuda", "h264", "h264", "yuv420p", 1920, 10, null, null, null);
        var k2 = HwAccel.ProbeCacheKey("cuda", "h264", "h264", "yuv420p", 1920, 8, null, null, null);
        Assert.NotEqual(k1, k2); // 位深必须进键（历史回归）
        var k3 = HwAccel.ProbeCacheKey("cuda", "h264", "h264", "yuv420p", 1920, null, null, 1.5, null);
        var k4 = HwAccel.ProbeCacheKey("cuda", "h264", "h264", "yuv420p", 1920, null, null, null, null);
        Assert.NotEqual(k3, k4); // speed≠1 必须进键
        var k5 = HwAccel.ProbeCacheKey("cuda", "h264", "h264", "yuv420p", 1920, null, null, 1, null);
        Assert.Equal(k4, k5);    // speed==1 与 null 同键
    }
}
