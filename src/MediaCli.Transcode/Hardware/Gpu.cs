using System.Text.RegularExpressions;
using MediaCli.Transcode.Bin;
using MediaCli.Transcode.Model;

namespace MediaCli.Transcode.Hardware;

/// <summary>
/// Port of gpu.js: GPU detection + NVIDIA encode/decode support matrices.
/// 数据来源：NVIDIA 官方 Video Encode/Decode Support Matrix（GeForce 10/20/30/40/50 系）。
/// 预筛规则：只有矩阵为明确 "no" 的组合才跳过探测；partial/unknown 一律放行。
/// </summary>
public static partial class Gpu
{
    public const int GenPascal = 10;
    public const int GenTuring = 20;
    public const int GenAmpere = 30;
    public const int GenAda = 40;
    public const int GenBlackwell = 50;

    public static readonly IReadOnlyDictionary<int, string> NvidiaGenNames =
        new Dictionary<int, string>
        {
            [GenPascal] = "Pascal",
            [GenTuring] = "Turing",
            [GenAmpere] = "Ampere",
            [GenAda] = "Ada Lovelace",
            [GenBlackwell] = "Blackwell",
        };

    /// <summary>型号 → NVIDIA 代次（匹配优先级 50→40→30→2050→20→GTX16→GTX10）。</summary>
    public static int? NvidiaGenerationOf(string? model)
    {
        var m = (model ?? "").ToUpperInvariant();
        if (!m.Contains("NVIDIA") && !RxAnyGpu().IsMatch(m)) return null;
        if (RxRtx50().IsMatch(m)) return GenBlackwell;
        if (RxRtx40().IsMatch(m)) return GenAda;
        if (RxRtx30().IsMatch(m)) return GenAmpere;
        if (RxRtx2050().IsMatch(m)) return GenAmpere; // Ampere 硅片特例
        if (RxRtx20().IsMatch(m)) return GenTuring;
        if (RxGtx16().IsMatch(m)) return GenTuring; // Turing 6th gen
        if (RxGtx10().IsMatch(m)) return GenPascal;
        return null;
    }

    [GeneratedRegex(@"RTX\s*\d|GTX\s*\d")]
    private static partial Regex RxAnyGpu();
    [GeneratedRegex(@"\bRTX\s*50\d{2}")] private static partial Regex RxRtx50();
    [GeneratedRegex(@"\bRTX\s*40\d{2}")] private static partial Regex RxRtx40();
    [GeneratedRegex(@"\bRTX\s*30\d{2}")] private static partial Regex RxRtx30();
    [GeneratedRegex(@"\bRTX\s*2050\b")] private static partial Regex RxRtx2050();
    [GeneratedRegex(@"\bRTX\s*20\d{2}")] private static partial Regex RxRtx20();
    [GeneratedRegex(@"\bGTX\s*16\d{2}")] private static partial Regex RxGtx16();
    [GeneratedRegex(@"\bGTX\s*10\d{2}")] private static partial Regex RxGtx10();

    private static Dictionary<int, string> All() => new()
    {
        [GenPascal] = "yes", [GenTuring] = "yes", [GenAmpere] = "yes", [GenAda] = "yes", [GenBlackwell] = "yes",
    };

    private static Dictionary<int, string> Only50() => new()
    {
        [GenPascal] = "no", [GenTuring] = "no", [GenAmpere] = "no", [GenAda] = "no", [GenBlackwell] = "yes",
    };

    /// <summary>NVENC 编码支持矩阵（仅能力数据，不参与预筛）。</summary>
    public static readonly IReadOnlyDictionary<string, IReadOnlyDictionary<string, IReadOnlyDictionary<int, string>>> NvencMatrix =
        new Dictionary<string, IReadOnlyDictionary<string, IReadOnlyDictionary<int, string>>>(StringComparer.Ordinal)
        {
            ["h264"] = new Dictionary<string, IReadOnlyDictionary<int, string>>
            {
                ["yuv420p"] = All(),
                ["yuv422p"] = Only50(),
                ["yuv444p"] = All(),
                ["lossless"] = All(),
            },
            ["hevc"] = new Dictionary<string, IReadOnlyDictionary<int, string>>
            {
                ["yuv420p"] = All(),
                ["yuv422p"] = Only50(),
                ["yuv444p"] = All(),
                ["lossless"] = All(),
                ["bframe"] = new Dictionary<int, string>
                {
                    [GenPascal] = "no",
                    [GenTuring] = "partial", // GTX16(6th) 不支持 / RTX20(7th) 支持
                    [GenAmpere] = "yes",
                    [GenAda] = "yes",
                    [GenBlackwell] = "yes",
                },
            },
            ["av1"] = new Dictionary<string, IReadOnlyDictionary<int, string>>
            {
                ["yuv420p"] = new Dictionary<int, string>
                {
                    [GenPascal] = "no", [GenTuring] = "no", [GenAmpere] = "no", [GenAda] = "yes", [GenBlackwell] = "yes",
                },
            },
        };

    /// <summary>NVDEC 解码支持矩阵（参与 selectTier 预筛）。</summary>
    public static readonly IReadOnlyDictionary<string, IReadOnlyDictionary<string, IReadOnlyDictionary<int, string>>> NvdecMatrix =
        new Dictionary<string, IReadOnlyDictionary<string, IReadOnlyDictionary<int, string>>>(StringComparer.Ordinal)
        {
            ["mpeg1"] = new Dictionary<string, IReadOnlyDictionary<int, string>> { ["any"] = All() },
            ["mpeg2"] = new Dictionary<string, IReadOnlyDictionary<int, string>> { ["any"] = All() },
            ["mpeg4"] = new Dictionary<string, IReadOnlyDictionary<int, string>> { ["any"] = All() },
            ["vc1"] = new Dictionary<string, IReadOnlyDictionary<int, string>> { ["any"] = All() },
            ["vp8"] = new Dictionary<string, IReadOnlyDictionary<int, string>>
            {
                ["any"] = new Dictionary<int, string>
                {
                    [GenPascal] = "partial", [GenTuring] = "yes", [GenAmpere] = "yes", [GenAda] = "yes", [GenBlackwell] = "yes",
                },
            },
            ["vp9"] = new Dictionary<string, IReadOnlyDictionary<int, string>>
            {
                ["420-8bit"] = All(),
                ["420-10bit"] = new Dictionary<int, string>
                {
                    [GenPascal] = "partial", [GenTuring] = "yes", [GenAmpere] = "yes", [GenAda] = "yes", [GenBlackwell] = "yes",
                },
                ["420-12bit"] = new Dictionary<int, string>
                {
                    [GenPascal] = "partial", [GenTuring] = "yes", [GenAmpere] = "yes", [GenAda] = "yes", [GenBlackwell] = "yes",
                },
            },
            ["h264"] = new Dictionary<string, IReadOnlyDictionary<int, string>>
            {
                ["420-8bit"] = All(),
                ["420-10bit"] = Only50(),
                ["422-8bit"] = Only50(),
                ["422-10bit"] = Only50(),
            },
            ["hevc"] = new Dictionary<string, IReadOnlyDictionary<int, string>>
            {
                ["420-8bit"] = All(),
                ["420-10bit"] = All(),
                ["420-12bit"] = All(),
                ["422-8bit"] = Only50(),
                ["422-10bit"] = Only50(),
                ["422-12bit"] = Only50(),
                ["444-8bit"] = new Dictionary<int, string>
                {
                    [GenPascal] = "no", [GenTuring] = "yes", [GenAmpere] = "yes", [GenAda] = "yes", [GenBlackwell] = "yes",
                },
                ["444-10bit"] = new Dictionary<int, string>
                {
                    [GenPascal] = "no", [GenTuring] = "yes", [GenAmpere] = "yes", [GenAda] = "yes", [GenBlackwell] = "yes",
                },
                ["444-12bit"] = new Dictionary<int, string>
                {
                    [GenPascal] = "no", [GenTuring] = "yes", [GenAmpere] = "yes", [GenAda] = "yes", [GenBlackwell] = "yes",
                },
            },
            ["av1"] = new Dictionary<string, IReadOnlyDictionary<int, string>>
            {
                // 20 系 partial 来自 RTX 2050（Ampere 硅片）
                ["any"] = new Dictionary<int, string>
                {
                    [GenPascal] = "no", [GenTuring] = "partial", [GenAmpere] = "yes", [GenAda] = "yes", [GenBlackwell] = "yes",
                },
            },
        };

    /// <summary>像素格式 → 色度采样（420/422/444），无法识别返回 null。</summary>
    public static string? ChromaOfPixFmt(string? pixFmt)
    {
        var m = (pixFmt ?? "").ToLowerInvariant();
        if (Rx420().IsMatch(m)) return "420";
        if (Rx422().IsMatch(m)) return "422";
        if (Rx444().IsMatch(m)) return "444";
        return null;
    }

    [GeneratedRegex(@"yuv420|nv12|p010|yuv410")] private static partial Regex Rx420();
    [GeneratedRegex(@"yuv422|p210")] private static partial Regex Rx422();
    [GeneratedRegex(@"yuv444|p410")] private static partial Regex Rx444();

    /// <summary>像素格式 → 位深（8/10/12/16），显式位深优先。</summary>
    public static int BitDepthOfPixFmt(string? pixFmt, object? explicitBitDepth)
    {
        if (TryGetFinite(explicitBitDepth, out var n) && n > 0)
        {
            return n >= 16 ? 16 : n >= 12 ? 12 : n >= 10 ? 10 : 8;
        }
        var m = (pixFmt ?? "").ToLowerInvariant();
        if (RxP16().IsMatch(m)) return 16;
        if (RxP12().IsMatch(m)) return 12;
        if (RxP10().IsMatch(m)) return 10;
        return 8;
    }

    [GeneratedRegex(@"p16|16le|16be")] private static partial Regex RxP16();
    [GeneratedRegex(@"p12|12le|12be")] private static partial Regex RxP12();
    [GeneratedRegex(@"p10|10le|10be")] private static partial Regex RxP10();

    internal static bool TryGetFinite(object? value, out double number)
    {
        number = 0;
        switch (value)
        {
            case null: return false;
            case double d: number = d; break;
            case float f: number = f; break;
            case int i: number = i; break;
            case long l: number = l; break;
            case string s when double.TryParse(s, System.Globalization.CultureInfo.InvariantCulture, out var p): number = p; break;
            default: try { number = Convert.ToDouble(value); } catch { return false; } break;
        }
        return double.IsFinite(number);
    }

    /// <summary>归一化解码 codec 名（矩阵键），未知返回 null。</summary>
    public static string? NormalizeDecodeCodec(string? codec)
    {
        var c = (codec ?? "").ToLowerInvariant();
        if (RxH264().IsMatch(c)) return "h264";
        if (RxHevc().IsMatch(c)) return "hevc";
        if (RxAv1().IsMatch(c)) return "av1";
        if (RxVp9().IsMatch(c)) return "vp9";
        if (c.Contains("vp8")) return "vp8";
        if (RxMpeg1().IsMatch(c)) return "mpeg1";
        if (RxMpeg2().IsMatch(c)) return "mpeg2";
        if (RxMpeg4().IsMatch(c)) return "mpeg4";
        if (RxVc1().IsMatch(c)) return "vc1";
        return null;
    }

    [GeneratedRegex(@"h264|avc1|avc")] private static partial Regex RxH264();
    [GeneratedRegex(@"hevc|h265|hvc1")] private static partial Regex RxHevc();
    [GeneratedRegex(@"av01|av1")] private static partial Regex RxAv1();
    [GeneratedRegex(@"vp09|vp9")] private static partial Regex RxVp9();
    [GeneratedRegex(@"mpeg1|mpg1")] private static partial Regex RxMpeg1();
    [GeneratedRegex(@"mpeg2|mpg2|mp2v")] private static partial Regex RxMpeg2();
    [GeneratedRegex(@"mpeg4|mp4v|divx|xvid")] private static partial Regex RxMpeg4();
    [GeneratedRegex(@"vc-?1")] private static partial Regex RxVc1();

    /// <summary>查 NVDEC 解码支持：yes/no/partial，无矩阵数据返回 null（不预筛）。</summary>
    public static string? NvdecSupportOf(int generation, string? codec, string? pixFmt, object? explicitBitDepth)
    {
        var fam = NormalizeDecodeCodec(codec);
        if (fam is null || !NvdecMatrix.TryGetValue(fam, out var row)) return null;
        if (!row.TryGetValue("any", out var ver))
        {
            var chroma = ChromaOfPixFmt(pixFmt);
            if (chroma is null) return null;
            var depth = BitDepthOfPixFmt(pixFmt, explicitBitDepth);
            if (!row.TryGetValue($"{chroma}-{depth}bit", out ver) &&
                !row.TryGetValue($"{chroma}-8bit", out ver))
            {
                return null;
            }
        }
        return ver.TryGetValue(generation, out var support) ? support : null;
    }

    /// <summary>查 NVENC 编码支持（仅能力数据）：yes/no/partial，无数据返回 null。</summary>
    public static string? NvencSupportOf(int generation, string codecFamily, string format)
    {
        if (!NvencMatrix.TryGetValue(codecFamily, out var row)) return null;
        if (!row.TryGetValue(format, out var ver)) return null;
        return ver.TryGetValue(generation, out var support) ? support : null;
    }

    private static List<GpuInfo>? CachedGpus;

    /// <summary>
    /// 探测本机 GPU 列表（C# 端无 systeminformation，改用 nvidia-smi / WMI，
    /// 任何失败都降级为空列表，不影响主流程——与 JS 降级语义一致）。
    /// 排序：nvidia=3 &gt; amd=2 &gt; intel=1 &gt; other=0，独显优先。
    /// </summary>
    public static List<GpuInfo> DetectGpus(bool force = false)
    {
        if (CachedGpus is not null && !force) return CachedGpus;
        var gpus = new List<GpuInfo>();
        try
        {
            if (OperatingSystem.IsWindows())
            {
                // 1) nvidia-smi 优先（独显精确识别）
                var smi = FfmpegBin.Which("nvidia-smi");
                if (smi is not null)
                {
                    var (code, stdout, _) = FfmpegBin.RunCapture(smi,
                        ["--query-gpu=name,driver_version", "--format=csv,noheader"]);
                    if (code == 0)
                    {
                        foreach (var line in stdout.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                        {
                            var parts = line.Split(',');
                            if (parts.Length == 0) continue;
                            gpus.Add(new GpuInfo
                            {
                                Vendor = "nvidia",
                                Model = parts[0].Trim(),
                                Generation = NvidiaGenerationOf(parts[0]),
                                DriverVersion = parts.Length > 1 ? parts[1].Trim() : null,
                            });
                        }
                    }
                }
                // 2) WMI 补齐其余显卡
                var (psCode, psOut, _) = FfmpegBin.RunCapture(
                    "powershell",
                    ["-NoProfile", "-Command",
                        "Get-CimInstance Win32_VideoController | ForEach-Object { \"$($_.Name)|$($_.DriverVersion)\" }"],
                    timeoutMs: 20_000);
                if (psCode == 0)
                {
                    foreach (var line in psOut.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                    {
                        var idx = line.LastIndexOf('|');
                        if (idx <= 0) continue;
                        var model = line[..idx].Trim();
                        if (model.Length == 0) continue;
                        var vendor = NormalizeVendor(model);
                        if (vendor == "nvidia" && gpus.Any(g => g.Model == model)) continue;
                        gpus.Add(new GpuInfo
                        {
                            Vendor = vendor,
                            Model = model,
                            Generation = vendor == "nvidia" ? NvidiaGenerationOf(model) : null,
                            DriverVersion = line[(idx + 1)..].Trim() is { Length: > 0 } dv ? dv : null,
                        });
                    }
                }
            }
        }
        catch
        {
            gpus = [];
        }
        var sorted = gpus
            .Select(g => (g, Score: g.Vendor switch { "nvidia" => 3, "amd" => 2, "intel" => 1, _ => 0 }))
            .OrderByDescending(x => x.Score)
            .Select((x, i) => new GpuInfo
            {
                Vendor = x.g.Vendor,
                Model = x.g.Model,
                Generation = x.g.Generation,
                DriverVersion = x.g.DriverVersion,
                Bus = x.g.Bus,
                Primary = i == 0,
            })
            .ToList();
        CachedGpus = sorted;
        return sorted;
    }

    public static void ClearCache() => CachedGpus = null;

    /// <summary>归一化 GPU vendor（"Intel Corporation" 含 "ati" 子串 → intel 判断必须在 amd 之前）。</summary>
    public static string NormalizeVendor(string? raw)
    {
        var v = (raw ?? "").ToLowerInvariant();
        if (v.Contains("nvidia")) return "nvidia";
        if (v.Contains("intel")) return "intel";
        if (v.Contains("advanced micro devices") || v == "amd" || RxAtiWord().IsMatch(v) || v.StartsWith("ati"))
            return "amd";
        return "other";
    }

    [GeneratedRegex(@"\bati\b")] private static partial Regex RxAtiWord();

    /// <summary>生成硬件预探测列表（主 NVIDIA GPU 代次 → 全部解码/编码组合支持状态）。</summary>
    public static GpuProbe? GpuProbeList(IReadOnlyList<GpuInfo> gpus)
    {
        var nvidia = gpus.FirstOrDefault(g => g.Vendor == "nvidia" && g.Generation is not null);
        if (nvidia is null) return null;
        var gen = nvidia.Generation!.Value;
        var probe = new GpuProbe
        {
            Vendor = "nvidia",
            Generation = gen,
            Arch = NvidiaGenNames.TryGetValue(gen, out var arch) ? arch : null,
            Model = nvidia.Model,
        };
        foreach (var (fam, rows) in NvdecMatrix)
        {
            foreach (var (fmt, ver) in rows)
            {
                if (!ver.TryGetValue(gen, out var support) || support is null) continue;
                var (chroma, depth) = fmt == "any" ? (null, null) : SplitFmt(fmt);
                probe.Decode.Add(new GpuDecodeProbeEntry
                {
                    Codec = fam,
                    Chroma = chroma,
                    BitDepth = depth is not null ? int.Parse(depth.Replace("bit", "")) : null,
                    Support = support,
                });
            }
        }
        foreach (var (fam, rows) in NvencMatrix)
        {
            foreach (var (format, ver) in rows)
            {
                if (!ver.TryGetValue(gen, out var support) || support is null) continue;
                probe.Encode.Add(new GpuEncodeProbeEntry { Codec = fam, Format = format, Support = support });
            }
        }
        return probe;
    }

    private static (string? Chroma, string? Depth) SplitFmt(string fmt)
    {
        var parts = fmt.Split('-');
        return parts.Length == 2 ? (parts[0], parts[1]) : (null, null);
    }
}
