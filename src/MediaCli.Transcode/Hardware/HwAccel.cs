using System.Diagnostics;
using System.Text.RegularExpressions;
using MediaCli.Transcode.Bin;
using MediaCli.Transcode.Model;

namespace MediaCli.Transcode.Hardware;

/// <summary>buildVideoFilters 参数（对应 JS 对象字面量）。</summary>
public sealed class HwVideoFilterOptions
{
    public required TierDef Tier { get; init; }
    public Size? Size { get; init; }
    public double? Speed { get; init; }
    public double? Framerate { get; init; }
    public string PreFilters { get; init; } = "";
    public string PostFilters { get; init; } = "";
    public bool HasScale { get; init; } = true;
    public string CodecFamily { get; init; } = "h264";
    public string? PixFmt { get; init; }
    public int? BitDepth { get; init; }
}

/// <summary>buildEncoderArgs 参数。</summary>
public sealed class EncoderArgs
{
    public double Quality { get; init; } = 24;
    public long? Bitrate { get; init; }
    public long? MaxBitrate { get; init; }
    public string CodecFamily { get; init; } = "h264";
    public string? PixFmt { get; init; }
    public int? BitDepth { get; init; }
    public string? ForcedEncoder { get; init; }
    public TierDef? Tier { get; init; }
    public HashSet<string>? Encoders { get; init; }
    public bool Anime { get; init; }
}

/// <summary>buildLayerArgs / buildProbeArgs 参数。</summary>
public sealed class LayerArgs
{
    public required TierDef Tier { get; init; }
    public Size? Size { get; init; }
    public double? Speed { get; init; }
    public double? Framerate { get; init; }
    public bool HasAudio { get; init; } = true;
    public double Quality { get; init; } = 24;
    public long? Bitrate { get; init; }
    public long? MaxBitrate { get; init; }
    public string CodecFamily { get; init; } = "h264";
    public string? PixFmt { get; init; }
    public int? BitDepth { get; init; }
    public string? ForcedEncoder { get; init; }
    public HashSet<string>? Encoders { get; init; }
    public bool Anime { get; init; }
    public string InputPath { get; init; } = "";
}

/// <summary>probeLayer 参数。</summary>
public sealed class ProbeLayerOptions
{
    public required string FFmpegPath { get; init; }
    public required string InputPath { get; init; }
    public required TierDef Tier { get; init; }
    public required Size Size { get; init; }
    public string? PixFmt { get; init; }
    public int? BitDepth { get; init; }
    public string Codec { get; init; } = "";
    public string CodecFamily { get; init; } = "h264";
    public double? Speed { get; init; }
    public double? Framerate { get; init; }
    public bool? HasAudio { get; init; }
    public double Quality { get; init; } = 24;
    public long? Bitrate { get; init; }
    public long? MaxBitrate { get; init; }
    public string? ForcedEncoder { get; init; }
    public HashSet<string>? Encoders { get; init; }
    public bool Anime { get; init; }
    public int TimeoutMs { get; init; } = 15000;
    public bool UseCache { get; init; } = true;
}

/// <summary>selectTier 参数。</summary>
public sealed class SelectTierOptions
{
    public required HardwareCaps Caps { get; init; }
    public string? FFmpegPath { get; init; }
    public required string InputPath { get; init; }
    public int SrcW { get; init; }
    public int SrcH { get; init; }
    public string? PixFmt { get; init; }
    public int? BitDepth { get; init; }
    public string Codec { get; init; } = "";
    public string CodecFamily { get; init; } = "h264";
    public long Dimension { get; init; }
    public double? Speed { get; init; }
    public double? Framerate { get; init; }
    public bool? HasAudio { get; init; }
    public double Quality { get; init; } = 24;
    public long? Bitrate { get; init; }
    public long? MaxBitrate { get; init; }
    public string? ForcedEncoder { get; init; }
    public string DecodeMode { get; init; } = "auto";
    public string? Hwaccel { get; init; }
    public bool Strict { get; init; }
    public bool Anime { get; init; }
}

/// <summary>
/// Port of hwaccel.js: S-4 硬件加速分层与缩放参数。
/// 关键设计：输出编码器由 preset 决定，与输入位深无关；位深只影响哪一层能解码。
/// </summary>
public static partial class HwAccel
{
    public const string DecodeModeAuto = "auto";
    public const string DecodeModeGpu = "gpu";
    public const string DecodeModeCpu = "cpu";
    public const double SpeedMin = 0.5;
    public const double SpeedMax = 2.0;

    /// <summary>编码器矩阵：[层][输出 codec 族] → 编码器名（swdec 复用 cpu 行）。</summary>
    private static readonly Dictionary<string, IReadOnlyDictionary<string, string>> EncoderMatrix =
        new(StringComparer.Ordinal)
        {
            ["cuda"] = new Dictionary<string, string> { ["h264"] = "h264_nvenc", ["hevc"] = "hevc_nvenc", ["av1"] = "av1_nvenc" },
            ["qsv"] = new Dictionary<string, string> { ["h264"] = "h264_qsv", ["hevc"] = "hevc_qsv", ["av1"] = "av1_qsv", ["vp9"] = "vp9_qsv" },
            ["amf"] = new Dictionary<string, string> { ["h264"] = "h264_amf", ["hevc"] = "hevc_amf", ["av1"] = "av1_amf" },
            ["d3d"] = new Dictionary<string, string> { ["h264"] = "h264_nvenc", ["hevc"] = "hevc_nvenc", ["av1"] = "av1_nvenc" },
            ["cpu"] = new Dictionary<string, string>
            {
                ["h264"] = "libx264", ["hevc"] = "libx265", ["av1"] = "libsvtav1", ["vp9"] = "libvpx-vp9",
            },
        };

    /// <summary>swdec 层兜底行 = CPU 行（真实取值由 resolveTiers 按厂商注入 encoderRow）。</summary>
    private static IReadOnlyDictionary<string, string> SwdecRow => EncoderMatrix["cpu"];

    /// <summary>各 codec 族的编码器运行时回退候选（ENCODER_RUNTIME_FALLBACK）。</summary>
    private static readonly Dictionary<string, string[]> EncoderRuntimeFallback =
        new(StringComparer.Ordinal)
        {
            ["h264"] = ["libx264", "h264_nvenc", "h264_qsv", "h264_amf"],
            ["hevc"] = ["libx265", "hevc_nvenc", "hevc_qsv", "hevc_amf"],
            ["vp9"] = ["libvpx-vp9", "vp9_qsv"],
            ["av1"] = ["libsvtav1", "libaom-av1", "librav1e", "av1_nvenc", "av1_qsv"],
        };

    /// <summary>质量值归一化偏移表（3 段阶梯，[crf&lt;=26, crf28-34, crf&gt;=36]）。</summary>
    private static readonly Dictionary<string, int[]> QualityOffset =
        new(StringComparer.Ordinal)
        {
            ["avc_nvenc"] = [5, 4, 1],
            ["avc_qsv"] = [2, 3, 1],
            ["hevc_nvenc"] = [6, 6, 1],
            ["hevc_qsv"] = [-1, -1, 0],
        };

    /// <summary>标定表键 → 层名映射。</summary>
    private static readonly Dictionary<string, string> TierToCalibKey =
        new(StringComparer.Ordinal)
        {
            ["cuda"] = "nvenc", ["d3d"] = "nvenc", ["qsv"] = "qsv", ["amf"] = "amf",
            ["cpu"] = "x264", ["swdec"] = "x264",
        };

    /// <summary>VMAF 等值质量偏移（2026-09-23，纯查表）。</summary>
    private static readonly Dictionary<string, int> VmafQualityOffset =
        new(StringComparer.Ordinal)
        {
            ["cpu-h264"] = 0, ["cpu-hevc"] = 0, ["cpu-av1"] = 0, ["cpu-vp9"] = 0,
            ["hw-h264"] = 7, ["hw-hevc"] = 5, ["hw-av1"] = 0, ["hw-vp9"] = 0,
        };

    /// <summary>层级定义（按优先级），与 JS TIERS 逐项一致。</summary>
    public static readonly TierDef[] Tiers =
    [
        new TierDef
        {
            Name = "cuda", Vendor = "nvidia", Hwaccel = "cuda", HwFormat = "cuda",
            Filter = "scale_cuda", FilterArgs = "interp_algo=lanczos,format=cuda", RequiresFilter = true,
        },
        new TierDef
        {
            Name = "qsv", Vendor = "intel", Hwaccel = "qsv", HwFormat = "qsv",
            Filter = "scale_qsv", FilterArgs = "mode=hq", RequiresFilter = true,
        },
        new TierDef
        {
            Name = "amf", Vendor = "amd", Hwaccel = "d3d11va", HwFormat = null,
            Filter = "vpp_amf", FilterArgs = "scale_type=bicubic", RequiresFilter = true,
        },
        new TierDef
        {
            Name = "d3d", Vendor = "any", Hwaccel = "d3d11va", HwFormat = null,
            Filter = "scale", FilterArgs = "flags=lanczos", RequiresFilter = true,
        },
        new TierDef
        {
            Name = "swdec", Vendor = "any", Hwaccel = null, HwFormat = null,
            Filter = "scale", FilterArgs = "flags=lanczos", RequiresFilter = true,
        },
        new TierDef
        {
            Name = "cpu", Vendor = "any", Hwaccel = null, HwFormat = null,
            Filter = "scale", FilterArgs = "flags=lanczos", RequiresFilter = true,
        },
    ];

    // ------------------------------------------------------------------
    // Quality normalization
    // ------------------------------------------------------------------

    /// <summary>计算某层某 codec 族的质量偏移（3 段阶梯查表）。</summary>
    public static int QualityOffsetOf(string tierName, string codecFamily, double crf)
    {
        var fam = codecFamily == "h264" ? "avc" : codecFamily;
        var impl = TierToCalibKey.GetValueOrDefault(tierName, tierName);
        if (!QualityOffset.TryGetValue($"{fam}_{impl}", out var row)) return 0;
        if (crf <= 26) return row[0];
        if (crf <= 34) return row[1];
        return row[2];
    }

    /// <summary>质量值归一化：preset 声明的 CRF → 该层等效质量值（clamp 0..51）。</summary>
    public static int NormalizeQuality(string tierName, string codecFamily, double crf)
        => Math.Clamp((int)Math.Round(crf + QualityOffsetOf(tierName, codecFamily, crf)), 0, 51);

    /// <summary>跨族质量偏移（VMAF 等值，纯查表）。</summary>
    public static (int Offset, int? Adjusted) CalculateOffset(string family, string? from = null, double? quality = null)
    {
        var offset = VmafQualityOffset.GetValueOrDefault(family, 0) - VmafQualityOffset.GetValueOrDefault(from ?? "cpu-h264", 0);
        return (offset, quality is not null ? Math.Clamp((int)Math.Round(quality.Value + offset), 0, 51) : null);
    }

    /// <summary>从编码器名推断「编码器实现」（nvenc/qsv/amf/x264）。</summary>
    private static string EncoderCalibImpl(string? encoderName)
    {
        var e = (encoderName ?? "").ToLowerInvariant();
        if (e.Contains("nvenc")) return "nvenc";
        if (e.Contains("_qsv")) return "qsv";
        if (e.Contains("_amf")) return "amf";
        return "x264";
    }

    /// <summary>显式编码器的质量值换算（VMAF 等值偏移 → 原生质量值）。</summary>
    private static int NormalizeQualityForEncoder(string encoder, string codecFamily, double quality)
    {
        var impl = EncoderCalibImpl(encoder);
        var implGroup = impl == "x264" ? "cpu" : "hw";
        var (_, adjusted) = CalculateOffset($"{implGroup}-{codecFamily}", from: $"cpu-{codecFamily}", quality: quality);
        return adjusted ?? 0;
    }

    /// <summary>从编码器名推断输出 codec 族（av1/vp9 先于 hevc/h264 兜底）。</summary>
    public static string CodecFamilyOf(string? encoderName)
    {
        if (string.IsNullOrEmpty(encoderName)) return "h264";
        var e = encoderName;
        if (RxAv1Family().IsMatch(e)) return "av1";
        if (RxVp9Family().IsMatch(e)) return "vp9";
        return RxHevcFamily().IsMatch(e) ? "hevc" : "h264";
    }

    [GeneratedRegex(@"av1|av01", RegexOptions.IgnoreCase)]
    private static partial Regex RxAv1Family();
    [GeneratedRegex(@"vp9|vp09", RegexOptions.IgnoreCase)]
    private static partial Regex RxVp9Family();
    [GeneratedRegex(@"hevc|h265|x265|hvc1", RegexOptions.IgnoreCase)]
    private static partial Regex RxHevcFamily();

    /// <summary>解析 preset 的输出 codec 族（显式编码器 → videoCodecFamily → h264）。</summary>
    public static string CodecFamilyOfPreset(FFmpegPreset? preset)
    {
        if (preset is null) return "h264";
        var forced = preset.UserArgs.VideoCodec;
        if (!string.IsNullOrEmpty(forced) && forced != "copy") return CodecFamilyOf(forced);
        if (!string.IsNullOrEmpty(preset.VideoCodecFamily)) return CodecFamilyOf(preset.VideoCodecFamily);
        return "h264";
    }

    // ------------------------------------------------------------------
    // Size / speed / depth
    // ------------------------------------------------------------------

    /// <summary>对齐到偶数（四舍五入后取偶，与 ffmpeg scale 行为一致）。</summary>
    public static int ToEven(double x)
    {
        var v = (int)Math.Round(x);
        return v - (v % 2);
    }

    /// <summary>按「长边 = dimension」计算输出尺寸（禁止放大 + 偶数对齐）。</summary>
    public static Size CalcLongEdge(int srcW, int srcH, long dimension)
    {
        if (srcW <= 0 || srcH <= 0)
            throw new ArgumentException($"calcLongEdge: invalid source size {srcW}x{srcH}");
        if (dimension <= 0)
            throw new ArgumentException($"calcLongEdge: invalid dimension {dimension}");
        var target = (int)Math.Min(dimension, (long)Math.Max(srcW, srcH));
        var size = srcW >= srcH
            ? new Size(ToEven(target), ToEven(srcH * (double)target / srcW))
            : new Size(ToEven(srcW * (double)target / srcH), ToEven(target));
        // 极端参数下（如 --dimension 2，或源尺寸极扁如 3840x6）短边会被取偶为 0，
        // 生成 `scale=w=2:h=0` 这类非法滤镜并让 ffmpeg 报错。此处钳到最小可用边长 2，
        // 与偶数对齐约束一致（0 不是合法的偶数边长）。
        return new Size(Math.Max(2, size.W), Math.Max(2, size.H));
    }

    /// <summary>判断像素格式位深："8bit" | "10bit"（显式位深优先）。</summary>
    public static string BitDepthOf(string? pixFmt, object? explicitBitDepth)
    {
        if (Gpu.TryGetFinite(explicitBitDepth, out var n) && n > 0) return n >= 9 ? "10bit" : "8bit";
        if (string.IsNullOrEmpty(pixFmt)) return "8bit";
        return RxHiDepth().IsMatch(pixFmt) ? "10bit" : "8bit";
    }

    [GeneratedRegex(@"p10|p12|p16|10le|12le|16le|10be|12be|16be", RegexOptions.IgnoreCase)]
    private static partial Regex RxHiDepth();

    /// <summary>校验 speed 范围（0/1/null → 1；越界抛错）。</summary>
    public static double ValidateSpeed(double? speed)
    {
        if (speed is null or 0 or 1) return 1;
        var v = speed.Value;
        if (!double.IsFinite(v)) throw new ArgumentException($"invalid speed: {v}");
        if (v < SpeedMin || v > SpeedMax)
            throw new ArgumentException($"speed out of range [{SpeedMin}, {SpeedMax}]: {v}");
        return v;
    }

    /// <summary>位深分级：hi / 8 / unknown（unknown 按需要对齐处理，保守方向）。</summary>
    private static string DepthClassOf(string? pixFmt, int? bitDepth)
    {
        if (bitDepth is { } b && b > 0) return b >= 10 ? "hi" : "8";
        var p = pixFmt ?? "";
        if (p.Length == 0) return "unknown";
        if (RxHiDepth().IsMatch(p)) return "hi";
        if (RxKnown8Bit().IsMatch(p)) return "8";
        return "unknown"; // "YUV4:2:0" 等不含位深的形态
    }

    [GeneratedRegex(@"^(yuv420p|yuvj420p|yuv422p|yuvj422p|yuv444p|yuvj444p|yuv410p|yuv411p|nv12|nv21|nv16|nv24|gbrp|gray|rgb24|bgr24|rgba|bgra|argb|abgr|pal8|monow|monob)$", RegexOptions.IgnoreCase)]
    private static partial Regex RxKnown8Bit();

    /// <summary>是否需要位深对齐到 8bit（位深未知按需要对齐）。</summary>
    public static bool NeedsDepthAlign(string? pixFmt, int? bitDepth)
        => DepthClassOf(pixFmt, bitDepth) != "8";

    // ------------------------------------------------------------------
    // Filter assembly
    // ------------------------------------------------------------------

    /// <summary>scale 滤镜输出格式覆盖（10bit 源 + h264 目标，仅 cuda/qsv）。</summary>
    public static string? ScaleFormatOverride(TierDef? tier, string codecFamily = "h264", string? pixFmt = null, int? bitDepth = null)
    {
        if (tier is null || (tier.Name != "cuda" && tier.Name != "qsv")) return null;
        if (codecFamily != "h264") return null;
        return NeedsDepthAlign(pixFmt, bitDepth) ? "nv12" : null;
    }

    /// <summary>生成缩放滤镜串（filterArgs 按逗号拆分：选项段参与拼接，尾部独立滤镜原样保留）。</summary>
    public static string BuildScaleFilter(TierDef tier, Size? size, string? swFormat)
    {
        var parts = new List<string>();
        if (size is not null)
        {
            parts.Add($"w={size.W}");
            parts.Add($"h={size.H}");
        }
        var filterArgs = tier.FilterArgs ?? "";
        var segments = filterArgs.Split(',');
        var opts = segments.Length > 0 ? segments[0] : "";
        if (opts.Length > 0) parts.Add(opts);
        if (!string.IsNullOrEmpty(swFormat)) parts.Add($"format={swFormat}");
        if (parts.Count == 0) return "";
        var head = $"{tier.Filter}={string.Join(":", parts)}";
        return segments.Length > 1 ? $"{head},{string.Join(",", segments[1..])}" : head;
    }

    /// <summary>组装完整视频滤镜链（三段式：pre → setpts → scale → fps → post）。</summary>
    public static string BuildVideoFilters(HwVideoFilterOptions o)
    {
        var chain = new List<string>();
        var tier = o.Tier;
        // 硬件解码层（帧在显存）一旦要跑软件滤镜，必须先下载到系统内存
        var vramFrames = tier.HwFormat == "cuda" || tier.HwFormat == "qsv";
        var swDomain = vramFrames && (!string.IsNullOrEmpty(o.PreFilters) || !string.IsNullOrEmpty(o.PostFilters));
        if (swDomain)
        {
            var mustBe8Bit = ScaleFormatOverride(tier, o.CodecFamily, o.PixFmt, o.BitDepth) != null;
            var hiBit = BitDepthOf(o.PixFmt, o.BitDepth) == "10bit";
            var swFormat = mustBe8Bit ? "nv12" : hiBit ? "p010le" : "nv12";
            chain.AddRange(["hwdownload", $"format={swFormat}"]);
        }
        if (!string.IsNullOrEmpty(o.PreFilters)) chain.Add(o.PreFilters);
        var sp = ValidateSpeed(o.Speed);
        if (sp != 1) chain.Add($"setpts=PTS/{FormatSpeed(sp)}");
        if (swDomain)
        {
            if (o.HasScale && o.Size is not null)
            {
                chain.Add($"scale=w={o.Size.W}:h={o.Size.H}:flags=lanczos");
            }
        }
        else if (tier.RequiresFilter)
        {
            var swFormat = ScaleFormatOverride(tier, o.CodecFamily, o.PixFmt, o.BitDepth);
            if (o.HasScale || swFormat is not null)
            {
                var filter = BuildScaleFilter(tier, o.HasScale ? o.Size : null, swFormat);
                if (filter.Length > 0) chain.Add(filter);
            }
        }
        if (o.Framerate is > 0) chain.Add($"fps={FormatSpeed(o.Framerate.Value)}");
        if (!string.IsNullOrEmpty(o.PostFilters)) chain.Add(o.PostFilters);
        return string.Join(",", chain);
    }

    /// <summary>speed 数值格式化（与 JS 模板 ${speed} 一致：1.5 → "1.5"）。</summary>
    internal static string FormatSpeed(double v)
        => v == Math.Floor(v) ? ((long)v).ToString(System.Globalization.CultureInfo.InvariantCulture)
            : v.ToString(System.Globalization.CultureInfo.InvariantCulture);

    /// <summary>音频滤镜串（atempo；speed 限制在 [0.5,2.0]，无需链式拆分）。</summary>
    public static string BuildAudioFilters(double? speed)
    {
        var sp = ValidateSpeed(speed);
        return sp == 1 ? "" : $"atempo={FormatSpeed(sp)}";
    }

    public static bool NeedsComplexFilter(double? speed, bool hasAudio)
        => ValidateSpeed(speed) != 1 && hasAudio;

    // ------------------------------------------------------------------
    // Encoder args
    // ------------------------------------------------------------------

    /// <summary>选择编码器（tier.encoderRow 优先于静态矩阵行）。</summary>
    public static string PickEncoder(string tierName, string codecFamily = "h264", TierDef? tier = null)
    {
        IReadOnlyDictionary<string, string> matrix =
            tier?.EncoderRow ?? EncoderMatrix.GetValueOrDefault(tierName, EncoderMatrix["cpu"]);
        return matrix.GetValueOrDefault(codecFamily, matrix.GetValueOrDefault("h264", "libx264"));
    }

    /// <summary>依据运行时探测到的编码器集合回退编码器（候选全缺时保持原值）。</summary>
    private static string PickRuntimeEncoder(string encoder, string codecFamily, HashSet<string>? encoders)
    {
        if (encoders is null || encoders.Count == 0) return encoder;
        if (encoders.Contains(encoder)) return encoder;
        var list = EncoderRuntimeFallback.GetValueOrDefault(codecFamily, [encoder]);
        foreach (var alt in list)
        {
            if (encoders.Contains(alt)) return alt;
        }
        return encoder;
    }

    /// <summary>生成输入侧硬件加速参数（-hwaccel / -hwaccel_output_format）。</summary>
    public static string[] BuildHwaccelArgs(TierDef tier)
    {
        if (string.IsNullOrEmpty(tier.Hwaccel)) return [];
        return tier.HwFormat is not null
            ? ["-hwaccel", tier.Hwaccel, "-hwaccel_output_format", tier.HwFormat]
            : ["-hwaccel", tier.Hwaccel];
    }

    private static string Kb(long v) => $"{Math.Round((double)v / 1000)}K";

    /// <summary>
    /// 编码器参数块（含质量参数）。统一按「有无目标码率」分发两种码控模式：
    /// bitrate&gt;0 → VBR；为空 → CQ。参数块按 N-126733 真机核准，勿凭文档臆造。
    /// </summary>
    public static string[] BuildEncoderArgs(string tierName, EncoderArgs o)
    {
        var codecFamily = o.CodecFamily;
        var baseRow = EncoderMatrix.GetValueOrDefault(tierName, EncoderMatrix["cpu"]);
        var familyRow = o.Tier?.EncoderRow ?? baseRow;
        var rawEncoder = o.ForcedEncoder
            ?? familyRow.GetValueOrDefault(codecFamily, EncoderMatrix["cpu"].GetValueOrDefault(codecFamily, "libx264"));
        var encoder = o.ForcedEncoder is not null
            ? rawEncoder
            : PickRuntimeEncoder(rawEncoder, codecFamily, o.Encoders);
        var q = NormalizeQualityForEncoder(encoder, codecFamily, o.Quality);
        var args = new List<string> { "-c:v", encoder };
        var impl = EncoderCalibImpl(encoder);
        var b = o.Bitrate ?? 0;
        var m = (o.MaxBitrate is > 0 ? Math.Round((double)o.MaxBitrate.Value) : Math.Round(b * 1.5));
        var usingVbr = b > 0;

        switch (impl)
        {
            case "nvenc":
            {
                // NVIDIA：-rc vbr；-rc-lookahead 是场景自适应关键帧与自适应 B 帧的前提
                args.AddRange(["-rc", "vbr", "-tune", "hq", "-rc-lookahead", "30"]);
                if (usingVbr)
                {
                    args.AddRange(["-b:v", Kb(b), "-maxrate", Kb((long)m), "-bufsize", Kb((long)m)]);
                }
                else
                {
                    args.AddRange(["-cq", q.ToString(), "-b:v", "0"]);
                }
                if (o.Anime) args.AddRange(["-spatial-aq", "1", "-temporal-aq", "1"]);
                break;
            }
            case "qsv":
            {
                // Intel：CQ 用 -global_quality（ICQ）；VBR 用 -b:v
                if (usingVbr)
                {
                    args.AddRange(["-b:v", Kb(b), "-maxrate", Kb((long)m)]);
                }
                else
                {
                    args.AddRange(["-global_quality", q.ToString(), "-b:v", "0"]);
                }
                break;
            }
            case "amf":
            {
                // AMD：CQ 用 -rc qvbr + -qvbr_quality_level；VBR 用 -rc vbr_peak（无 A 卡未实拍验证）
                if (usingVbr)
                {
                    args.AddRange(["-rc", "vbr_peak", "-b:v", Kb(b), "-maxrate", Kb((long)m)]);
                }
                else
                {
                    args.AddRange(["-rc", "qvbr", "-qvbr_quality_level", q.ToString()]);
                }
                break;
            }
            default: // cpu
            {
                if (codecFamily is "h264" or "hevc")
                {
                    if (usingVbr)
                    {
                        args.AddRange(["-b:v", Kb(b), "-preset", "medium", "-maxrate", Kb((long)m), "-bufsize", Kb((long)m)]);
                    }
                    else
                    {
                        args.AddRange(["-crf", q.ToString(), "-preset", "medium"]);
                    }
                }
                else if (usingVbr)
                {
                    args.AddRange(["-b:v", Kb(b)]);
                }
                else
                {
                    args.AddRange(["-crf", q.ToString(), "-b:v", "0"]);
                }
                if (o.Anime)
                {
                    if (codecFamily == "h264")
                    {
                        args.AddRange(["-tune", "animation"]);
                    }
                    else if (codecFamily == "hevc")
                    {
                        args.AddRange(["-x265-params", "no-sao=1:aq-mode=3"]);
                    }
                    else if (codecFamily == "av1" && encoder.Contains("svtav1", StringComparison.Ordinal))
                    {
                        args.AddRange(["-svtav1-params", "tune=0"]);
                    }
                }
                break;
            }
        }

        // CQ 模式下显式 maxBitrate → 追加峰值封顶（amf 保守只发 -maxrate）
        if (!usingVbr && m > 0)
        {
            args.AddRange(["-maxrate", Kb((long)m)]);
            if (impl != "amf") args.AddRange(["-bufsize", Kb((long)m)]);
        }

        // swdec 层：10bit 源 + h264 目标需要 -pix_fmt yuv420p 对齐（hevc 不需要）
        if (tierName == "swdec" && o.ForcedEncoder is null &&
            codecFamily == "h264" && NeedsDepthAlign(o.PixFmt, o.BitDepth))
        {
            args.AddRange(["-pix_fmt", "yuv420p"]);
        }
        return [.. args];
    }

    /// <summary>生成单层的完整 ffmpeg 参数（不含输入输出路径）。</summary>
    public static (string[] InputArgs, string[] OutputArgs, string Encoder) BuildLayerArgs(LayerArgs o)
    {
        var inputArgs = BuildHwaccelArgs(o.Tier);
        var outputArgs = new List<string>();
        var vf = BuildVideoFilters(new HwVideoFilterOptions
        {
            Tier = o.Tier,
            Size = o.Size,
            Speed = o.Speed,
            Framerate = o.Framerate,
            CodecFamily = o.CodecFamily,
            PixFmt = o.PixFmt,
            BitDepth = o.BitDepth,
        });
        var af = BuildAudioFilters(o.Speed);
        if (vf.Length > 0) outputArgs.AddRange(["-vf", vf]);
        if (af.Length > 0 && o.HasAudio) outputArgs.AddRange(["-af", af]);
        var encArgs = BuildEncoderArgs(o.Tier.Name, new EncoderArgs
        {
            Quality = o.Quality,
            Bitrate = o.Bitrate,
            MaxBitrate = o.MaxBitrate,
            CodecFamily = o.CodecFamily,
            PixFmt = o.PixFmt,
            BitDepth = o.BitDepth,
            ForcedEncoder = o.ForcedEncoder,
            Tier = o.Tier,
            Encoders = o.Encoders,
            Anime = o.Anime,
        });
        outputArgs.AddRange(encArgs);
        return ([.. inputArgs], [.. outputArgs], encArgs[1]);
    }

    /// <summary>组装探测用参数（探测即干跑，与真实命令同构；帧数 10）。</summary>
    public static string[] BuildProbeArgs(LayerArgs o)
    {
        var (inputArgs, outputArgs, _) = BuildLayerArgs(o);
        var args = new List<string> { "-hide_banner", "-v", "error", "-y" };
        args.AddRange(inputArgs);
        args.Add("-i");
        args.Add(o.InputPath);
        args.AddRange(outputArgs);
        args.AddRange(["-frames:v", "10", "-f", "null", "-"]);
        return [.. args];
    }

    // ------------------------------------------------------------------
    // Probe & tier selection
    // ------------------------------------------------------------------

    private static readonly Dictionary<string, bool> ProbeCache = new(StringComparer.Ordinal);

    /// <summary>探测缓存键（层|编码|族|像素格式|尺寸档|位深|显式编码器|speed|fps|anime|quality）。</summary>
    public static string ProbeCacheKey(
        string tierName, string codec, string codecFamily, string? pixFmt, long dimension,
        int? bitDepth, string? forcedEncoder, double? speed, double? framerate, bool anime = false, double? quality = null)
    {
        var speedKey = speed is > 0 && speed != 1 ? HwAccel.speedKey(speed.Value) : "";
        var fpsKey = framerate is > 0 ? HwAccel.speedKey(framerate.Value) : "";
        var qualityKey = quality is > 0 ? HwAccel.speedKey(quality.Value) : "";
        return $"{tierName}|{codec}|{codecFamily}|{pixFmt}|{dimension}|{(bitDepth?.ToString() ?? "")}|{forcedEncoder ?? ""}|{speedKey}|{fpsKey}|{(anime ? "anime" : "")}|{qualityKey}";
    }

    private static string speedKey(double v) => v.ToString(System.Globalization.CultureInfo.InvariantCulture);

    public static void ClearProbeCache() => ProbeCache.Clear();

    /// <summary>
    /// 探测某一层是否可用：与真实命令同构（同一 buildLayerArgs），只改帧数与输出。
    /// 判定只看退出码；必须带超时。只缓存「探测成功」。
    /// </summary>
    public static bool ProbeLayer(ProbeLayerOptions o)
    {
        var key = ProbeCacheKey(o.Tier.Name, o.Codec, o.CodecFamily, o.PixFmt, o.Size.W,
            o.BitDepth, o.ForcedEncoder, o.Speed, o.Framerate, o.Anime, o.Quality);
        if (o.UseCache && ProbeCache.TryGetValue(key, out var cached)) return cached;

        var bin = !string.IsNullOrEmpty(o.FFmpegPath) ? o.FFmpegPath : FfmpegBin.Which("ffmpeg");
        if (bin is null)
        {
            if (o.UseCache) ProbeCache[key] = false;
            return false;
        }

        var args = BuildProbeArgs(new LayerArgs
        {
            Tier = o.Tier,
            Size = o.Size,
            Speed = o.Speed,
            Framerate = o.Framerate,
            HasAudio = o.HasAudio ?? true,
            Quality = o.Quality,
            Bitrate = o.Bitrate,
            MaxBitrate = o.MaxBitrate,
            CodecFamily = o.CodecFamily,
            PixFmt = o.PixFmt,
            BitDepth = o.BitDepth,
            ForcedEncoder = o.ForcedEncoder,
            Encoders = o.Encoders,
            Anime = o.Anime,
            InputPath = o.InputPath,
        });

        var ok = RunProbe(bin, args, o.TimeoutMs);
        if (o.UseCache && ok) ProbeCache[key] = ok;
        return ok;
    }

    private static bool RunProbe(string bin, IReadOnlyList<string> args, int timeoutMs)
    {
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = bin,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
            };
            foreach (var a in args) psi.ArgumentList.Add(a);
            using var proc = Process.Start(psi)!;
            _ = proc.StandardOutput.ReadToEndAsync();
            _ = proc.StandardError.ReadToEndAsync();
            if (!proc.WaitForExit(timeoutMs))
            {
                try { proc.Kill(entireProcessTree: true); } catch { /* ignore */ }
                return false;
            }
            return proc.ExitCode == 0;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>解析候选层并注入 swdec 的厂商编码器行。</summary>
    public static IReadOnlyList<TierDef> ResolveTiers(
        HardwareCaps caps, string decodeMode = DecodeModeAuto, string? hwaccel = null)
    {
        var names = HwDetect.CandidateTiers(caps, decodeMode, hwaccel);
        var vendor = HwDetect.PrimaryVendor(caps);
        var tiers = new List<TierDef>();
        foreach (var n in names)
        {
            var t = Array.Find(Tiers, x => x.Name == n)
                ?? throw new InvalidOperationException($"resolveTiers: unknown tier '{n}'");
            if (n == "swdec" && HwDetect.SwdecEncodersByVendor.TryGetValue(vendor, out var row))
            {
                tiers.Add(new TierDef
                {
                    Name = t.Name, Vendor = t.Vendor, Hwaccel = t.Hwaccel, HwFormat = t.HwFormat,
                    Filter = t.Filter, FilterArgs = t.FilterArgs, RequiresFilter = t.RequiresFilter,
                    EncoderRow = row,
                });
            }
            else
            {
                tiers.Add(t);
            }
        }
        return tiers;
    }

    /// <summary>GPU 矩阵预筛：仅 auto 模式、cuda/d3d 层、矩阵明确 no 才拦截。</summary>
    private static bool GpuBlocksDecode(
        HardwareCaps caps, TierDef tier, string codec, string? pixFmt, int? bitDepth, string decodeMode)
    {
        var probe = caps.GpuProbe;
        if (probe is null) return false;
        if (decodeMode != DecodeModeAuto) return false;
        if (tier.Name != "cuda" && tier.Name != "d3d") return false;
        return Gpu.NvdecSupportOf(probe.Generation, codec, pixFmt, bitDepth) == "no";
    }

    /// <summary>
    /// 主入口：双层决策 —— 硬件检测筛候选 → 文件探测定层。
    /// strict 模式不降级；gpu 模式失败即硬失败；auto 全部失败回退 cpu。
    /// </summary>
    public static HwPlan SelectTier(SelectTierOptions o)
    {
        var size = CalcLongEdge(o.SrcW, o.SrcH, o.Dimension);
        var tiers = ResolveTiers(o.Caps, o.DecodeMode, o.Hwaccel);

        if (o.Strict && o.DecodeMode != DecodeModeCpu)
        {
            var hwTiers = tiers.Where(t => t.Hwaccel is not null).ToList();
            if (hwTiers.Count == 0)
            {
                throw new InvalidOperationException(
                    "strict mode: no hardware acceleration tier available on this machine " +
                    $"(vendor={o.Caps.Vendor}, usable={string.Join(",", o.Caps.Usable.Select(kv => kv.Key + "=" + kv.Value))}). " +
                    "Use --decode-mode cpu to force software decode.");
            }
            var primary = hwTiers[0];
            var ok = ProbeLayer(new ProbeLayerOptions
            {
                FFmpegPath = o.FFmpegPath ?? o.Caps.FFmpegPath,
                InputPath = o.InputPath,
                Tier = primary,
                Size = size,
                PixFmt = o.PixFmt,
                BitDepth = o.BitDepth,
                Codec = o.Codec,
                CodecFamily = o.CodecFamily,
                Speed = o.Speed,
                Framerate = o.Framerate,
                HasAudio = o.HasAudio,
                Quality = o.Quality,
                Bitrate = o.Bitrate,
                MaxBitrate = o.MaxBitrate,
                ForcedEncoder = o.ForcedEncoder,
                Encoders = o.Caps.Encoders,
                Anime = o.Anime,
            });
            if (!ok)
            {
                throw new InvalidOperationException(
                    $"strict mode: hwaccel '{primary.Name}' unavailable for this input " +
                    $"(size={o.SrcW}x{o.SrcH}, pix_fmt={o.PixFmt}, codec={o.Codec}). " +
                    "Refusing to fall back to CPU; use --decode-mode cpu to force software decode.");
            }
            return new HwPlan
            {
                Tier = primary, Size = size, Degraded = false, Tried = [primary.Name],
                Reason = $"matched '{primary.Name}' (strict)",
            };
        }

        var tried = new List<string>();
        var failures = new List<string>();
        foreach (var tier in tiers)
        {
            tried.Add(tier.Name);
            if (GpuBlocksDecode(o.Caps, tier, o.Codec, o.PixFmt, o.BitDepth, o.DecodeMode))
            {
                failures.Add(tier.Name);
                continue;
            }
            var ok = ProbeLayer(new ProbeLayerOptions
            {
                FFmpegPath = o.FFmpegPath ?? o.Caps.FFmpegPath,
                InputPath = o.InputPath,
                Tier = tier,
                Size = size,
                PixFmt = o.PixFmt,
                BitDepth = o.BitDepth,
                Codec = o.Codec,
                CodecFamily = o.CodecFamily,
                Speed = o.Speed,
                Framerate = o.Framerate,
                HasAudio = o.HasAudio,
                Quality = o.Quality,
                Bitrate = o.Bitrate,
                MaxBitrate = o.MaxBitrate,
                ForcedEncoder = o.ForcedEncoder,
                Encoders = o.Caps.Encoders,
                Anime = o.Anime,
            });
            if (ok)
            {
                return new HwPlan
                {
                    Tier = tier,
                    Size = size,
                    Degraded = tried.Count > 1,
                    Tried = [.. tried],
                    Reason = tried.Count > 1
                        ? $"degraded to '{tier.Name}' after {string.Join(",", tried.Take(tried.Count - 1))} failed"
                        : $"matched '{tier.Name}'",
                };
            }
            failures.Add(tier.Name);
            if (o.DecodeMode == DecodeModeGpu)
            {
                throw new InvalidOperationException(
                    $"hwaccel '{tier.Name}' unavailable for this input " +
                    $"(size={o.SrcW}x{o.SrcH}, pix_fmt={o.PixFmt}, codec={o.Codec}). " +
                    "Try --decode-mode auto or cpu.");
            }
        }

        if (o.Strict)
        {
            throw new InvalidOperationException(
                "strict mode: no decode tier usable for this input " +
                $"(tried=[{string.Join(",", tried)}], size={o.SrcW}x{o.SrcH}, pix_fmt={o.PixFmt}, codec={o.Codec}). " +
                "Refusing automatic fallback.");
        }
        var cpuTier = Array.Find(Tiers, t => t.Name == "cpu")!;
        return new HwPlan
        {
            Tier = cpuTier,
            Size = size,
            Degraded = true,
            Tried = [.. tried],
            Reason = $"all tiers failed ({string.Join(", ", failures)}); falling back to cpu for real attempt",
        };
    }
}
