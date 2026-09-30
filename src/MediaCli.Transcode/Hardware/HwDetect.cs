using System.Text.RegularExpressions;
using MediaCli.Transcode.Bin;
using MediaCli.Transcode.Model;

namespace MediaCli.Transcode.Hardware;

/// <summary>
/// Port of hwdetect.js: device-level capability detection from the ffmpeg build
/// itself ("这台机器有哪些可用的硬件加速器"). Result cached per process.
/// </summary>
public static partial class HwDetect
{
    private static HardwareCaps? CachedCapabilities;

    /// <summary>各层对应的「可用性指纹」编码器。</summary>
    private static readonly Dictionary<string, string[]> TierEncoderProbe = new(StringComparer.Ordinal)
    {
        ["cuda"] = ["h264_nvenc", "hevc_nvenc"],
        ["qsv"] = ["h264_qsv", "hevc_qsv"],
        ["amf"] = ["h264_amf", "hevc_amf"],
        ["d3d"] = ["h264_nvenc", "hevc_nvenc", "h264_qsv", "hevc_qsv"],
    };

    /// <summary>层名 → caps.FilterSupport 键（T5：该层缩放滤镜必须被构建支持）。</summary>
    private static readonly Dictionary<string, string> FilterSupportKey = new(StringComparer.Ordinal)
    {
        ["cuda"] = "scale_cuda",
        ["qsv"] = "scale_qsv",
        ["amf"] = "vpp_amf",
    };

    /// <summary>GPU 厂商 → 理论可用的硬件解/编码层（T6）。</summary>
    public static readonly IReadOnlyDictionary<string, string[]> GpuVendorHwaccels =
        new Dictionary<string, string[]>(StringComparer.Ordinal)
        {
            ["nvidia"] = ["cuda", "d3d"],
            ["intel"] = ["qsv", "d3d"],
            ["amd"] = ["amf", "d3d"],
            ["other"] = ["d3d"],
        };

    /// <summary>swdec 层（CPU 解码 + CPU scale + 硬件编码）按厂商的编码器矩阵。</summary>
    public static readonly IReadOnlyDictionary<string, IReadOnlyDictionary<string, string>> SwdecEncodersByVendor =
        new Dictionary<string, IReadOnlyDictionary<string, string>>(StringComparer.Ordinal)
        {
            ["nvidia"] = new Dictionary<string, string> { ["h264"] = "h264_nvenc", ["hevc"] = "hevc_nvenc", ["av1"] = "av1_nvenc" },
            ["intel"] = new Dictionary<string, string> { ["h264"] = "h264_qsv", ["hevc"] = "hevc_qsv", ["av1"] = "av1_qsv" },
            ["amd"] = new Dictionary<string, string> { ["h264"] = "h264_amf", ["hevc"] = "hevc_amf", ["av1"] = "av1_amf" },
        };

    /// <summary>层名 → ffmpeg -hwaccels 输出中的解码方法名（amf/d3d 均走 d3d11va 系列）。</summary>
    private static readonly Dictionary<string, string[]> TierHwaccelNames = new(StringComparer.Ordinal)
    {
        ["cuda"] = ["cuda"],
        ["qsv"] = ["qsv"],
        ["amf"] = ["d3d11va", "d3d12va", "dxva2"],
        ["d3d"] = ["d3d11va", "d3d12va", "dxva2"],
    };

    private static readonly Dictionary<string, string> HwaccelAliases = new(StringComparer.Ordinal)
    {
        ["d3d11va"] = "d3d", ["d3d12va"] = "d3d", ["dxva2"] = "d3d",
    };
    private static readonly HashSet<string> HwaccelNames = ["cuda", "qsv", "amf", "d3d", "cpu"];

    // ------------------------------------------------------------------
    // Output parsers
    // ------------------------------------------------------------------

    /// <summary>
    /// 解析 ffmpeg -encoders 输出（" V....D h264_nvenc   ..."）。
    ///
    /// 注意排除表头说明行（" V..... = Video"）：它的前 6 位同样形如标志位，
    /// 若直接取第 7 列会把 "=" 当成编码器名，污染集合（曾导致 Encoders 含 "="、
    /// EncoderCount 偏大，并使"空集合=探测失败"这类判断失效）。
    /// 编码器名以字母/数字开头，因此此处名首字符必须非 '='。
    /// </summary>
    public static HashSet<string> ParseEncoders(string stdout)
    {
        var set = new HashSet<string>(StringComparer.Ordinal);
        foreach (var line in stdout.Split(["\r\n", "\n"], StringSplitOptions.None))
        {
            var m = RxEncoderLine().Match(line);
            if (m.Success) set.Add(m.Groups[1].Value);
        }
        return set;
    }

    [GeneratedRegex(@"^\s*[A-Z.]{6}\s+([A-Za-z0-9_][\w.\-]*)")]
    private static partial Regex RxEncoderLine();

    /// <summary>解析 ffmpeg -hwaccels 输出。</summary>
    public static HashSet<string> ParseHwaccels(string stdout)
    {
        var set = new HashSet<string>(StringComparer.Ordinal);
        foreach (var line in stdout.Split(["\r\n", "\n"], StringSplitOptions.None))
        {
            var t = line.Trim();
            if (t.Length == 0 || RxHwaccelHeader().IsMatch(t)) continue;
            set.Add(t);
        }
        return set;
    }

    [GeneratedRegex(@"^hardware acceleration methods:?$", RegexOptions.IgnoreCase)]
    private static partial Regex RxHwaccelHeader();

    /// <summary>解析 ffmpeg -filters 输出：找含 -&gt; 的列，取它前一列。</summary>
    public static HashSet<string> ParseFilters(string stdout)
    {
        var set = new HashSet<string>(StringComparer.Ordinal);
        foreach (var line in stdout.Split(["\r\n", "\n"], StringSplitOptions.None))
        {
            if (!line.Contains("->")) continue;
            var cols = line.Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries);
            var arrowIdx = Array.FindIndex(cols, c => c.Contains("->"));
            if (arrowIdx >= 1 && RxFilterName().IsMatch(cols[arrowIdx - 1]))
            {
                set.Add(cols[arrowIdx - 1]);
            }
        }
        return set;
    }

    [GeneratedRegex(@"^[A-Za-z0-9_]+$")]
    private static partial Regex RxFilterName();

    /// <summary>解析 ffmpeg -version 输出：版本标识与 configuration 串。</summary>
    public static (string Version, string Configuration) ParseVersionInfo(string stdout)
    {
        var lines = stdout.Split(["\r\n", "\n"], StringSplitOptions.None);
        var first = RxVersionLine().Match(lines.Length > 0 ? lines[0] : "");
        var version = first.Success ? first.Groups[1].Value.Trim() : "";
        var cfgLine = lines.FirstOrDefault(l => l.Contains("configuration:")) ?? "";
        var configuration = cfgLine.Contains(':') ? cfgLine[(cfgLine.IndexOf(':') + 1)..].Trim() : "";
        return (version, configuration);
    }

    [GeneratedRegex(@"^ffmpeg version\s+(\S[\S ]*?)\s+Copyright", RegexOptions.IgnoreCase)]
    private static partial Regex RxVersionLine();

    // ------------------------------------------------------------------
    // Device probes
    // ------------------------------------------------------------------

    /// <summary>真实设备初始化探测：lavfi testsrc2 1 帧到 null，只看退出码，8s 超时。</summary>
    private static bool ProbeDeviceUsable(string bin, string? hwaccel, string encoder)
    {
        var args = new List<string> { "-hide_banner", "-v", "error", "-y" };
        if (!string.IsNullOrEmpty(hwaccel)) args.AddRange(["-hwaccel", hwaccel]);
        args.AddRange(
        [
            "-f", "lavfi", "-i", "testsrc2=size=320x240:rate=1", "-t", "1",
            "-c:v", encoder, "-frames:v", "1", "-f", "null", "-",
        ]);
        var (code, _, _) = FfmpegBin.RunCapture(bin, args, timeoutMs: 8000);
        return code == 0;
    }

    /// <summary>检测本机硬件加速能力（进程内缓存；非 force 复用缓存）。</summary>
    public static HardwareCaps DetectHardwareCapabilities(
        string? ffmpegPath = null, bool force = false, bool deviceProbe = true)
    {
        if (CachedCapabilities is not null && !force) return CachedCapabilities;

        var bin = ffmpegPath ?? FfmpegBin.ResolveFFmpegBinary();
        if (string.IsNullOrEmpty(bin))
        {
            throw new InvalidOperationException("hwdetect: ffmpeg not found");
        }

        // 1. 静态能力：version / encoders / hwaccels / filters + GPU 列表（限定 8s 超时防止死锁）
        var (vc, verOut, _) = FfmpegBin.RunCapture(bin, ["-hide_banner", "-version"], timeoutMs: 8000);
        var (ec, encOut, _) = FfmpegBin.RunCapture(bin, ["-hide_banner", "-v", "error", "-encoders"], timeoutMs: 8000);
        var (hc, hwOut, _) = FfmpegBin.RunCapture(bin, ["-hide_banner", "-v", "error", "-hwaccels"], timeoutMs: 8000);
        var (fc, fltOut, _) = FfmpegBin.RunCapture(bin, ["-hide_banner", "-v", "error", "-filters"], timeoutMs: 8000);
        if (vc != 0 && verOut.Length == 0) { /* 继续用空输出走降级 */ }
        _ = (ec, hc, fc);
        var (version, configuration) = ParseVersionInfo(verOut);
        var encoders = ParseEncoders(encOut);
        var hwaccels = ParseHwaccels(hwOut);
        var filters = ParseFilters(fltOut);
        var gpus = Gpu.DetectGpus();

        // 2. 逐层判定「静态是否具备」
        var staticOk = new Dictionary<string, bool>
        {
            ["cuda"] = TierEncoderProbe["cuda"].All(encoders.Contains),
            ["qsv"] = TierEncoderProbe["qsv"].All(encoders.Contains),
            ["amf"] = TierEncoderProbe["amf"].All(encoders.Contains),
            ["d3d"] = hwaccels.Contains("d3d11va"),
            ["cpu"] = true,
        };

        // 3. 真实设备探测（静态具备的层再确认设备真的能用）
        var usable = new Dictionary<string, bool>(staticOk);
        var probeDetail = new Dictionary<string, string>(StringComparer.Ordinal);
        if (deviceProbe)
        {
            if (staticOk["cuda"])
            {
                var ok = ProbeDeviceUsable(bin, "cuda", "h264_nvenc");
                usable["cuda"] = ok;
                probeDetail["cuda"] = ok ? "device ok" : "device init failed";
            }
            if (staticOk["qsv"])
            {
                var ok = ProbeDeviceUsable(bin, "qsv", "h264_qsv");
                usable["qsv"] = ok;
                probeDetail["qsv"] = ok ? "device ok" : "device init failed";
            }
            if (staticOk["amf"])
            {
                // AMF 无独立解码 hwaccel，直接测编码器
                var ok = ProbeDeviceUsable(bin, null, "h264_amf");
                usable["amf"] = ok;
                probeDetail["amf"] = ok ? "device ok" : "DLL/device unavailable (e.g. amfrt64.dll)";
            }
            if (staticOk["d3d"])
            {
                // d3d 只做解码，配 libx264 编码验证解码链路
                var ok = ProbeDeviceUsable(bin, "d3d11va", "libx264");
                usable["d3d"] = ok;
                probeDetail["d3d"] = ok ? "device ok" : "device init failed";
            }
        }

        // 4. 推导厂商
        var vendor = usable["cuda"] ? "nvidia" : usable["qsv"] ? "intel" : usable["amf"] ? "amd" : "any";

        // 5. 滤镜可用性
        var filterSupport = new Dictionary<string, bool>
        {
            ["scale_cuda"] = filters.Contains("scale_cuda"),
            ["scale_qsv"] = filters.Contains("scale_qsv"),
            ["vpp_amf"] = filters.Contains("vpp_amf"),
            ["scale_d3d11"] = filters.Contains("scale_d3d11"),
            ["scale_vulkan"] = filters.Contains("scale_vulkan"),
        };

        // 6. GPU 预探测列表（仅 NVIDIA 有数据）
        var gpuProbe = Gpu.GpuProbeList(gpus);

        CachedCapabilities = new HardwareCaps
        {
            FFmpegPath = bin,
            Version = version,
            Configuration = configuration,
            BuildKind = RegexNonfree().IsMatch(configuration) ? "nonfree"
                : RegexGpl().IsMatch(configuration) ? "gpl" : "default",
            Encoders = encoders,
            Filters = filters,
            Vendor = vendor,
            Usable = usable,
            StaticOk = staticOk,
            ProbeDetail = probeDetail,
            Hwaccels = [.. hwaccels],
            FilterSupport = filterSupport,
            EncoderCount = encoders.Count,
            Gpus = gpus,
            GpuProbe = gpuProbe,
        };
        return CachedCapabilities;
    }

    [GeneratedRegex(@"nonfree", RegexOptions.IgnoreCase)]
    private static partial Regex RegexNonfree();
    [GeneratedRegex(@"gpl", RegexOptions.IgnoreCase)]
    private static partial Regex RegexGpl();

    public static void ClearCache() => CachedCapabilities = null;

    /// <summary>主 GPU 厂商（candidateTiers auto 链的定向依据）。</summary>
    public static string PrimaryVendor(HardwareCaps caps)
    {
        var v = caps.Gpus.Count > 0 ? caps.Gpus[0].Vendor : caps.Vendor is "any" ? "other" : caps.Vendor;
        return v == "any" ? "other" : v;
    }

    private static bool SwdecUsable(HardwareCaps caps, string vendor)
    {
        if (!SwdecEncodersByVendor.TryGetValue(vendor, out var row)) return false;
        return row.Values.Any(caps.Encoders.Contains);
    }

    private static bool TierWhitelisted(IReadOnlyList<string> hwaccels, string tierName)
    {
        if (!TierHwaccelNames.TryGetValue(tierName, out var names)) return true;
        if (hwaccels.Count == 0) return true;
        return names.Any(hwaccels.Contains);
    }

    /// <summary>--hwaccel 取值 → 层名（非法/未知值返回 null；"auto" 归一化为 "d3d" 老陷阱）。</summary>
    private static string? NormalizeHwaccelName(string? hwaccel)
    {
        if (string.IsNullOrEmpty(hwaccel)) return null;
        var h = hwaccel.ToLowerInvariant();
        if (h == "auto") return "d3d";
        if (HwaccelAliases.TryGetValue(h, out var alias)) return alias;
        return HwaccelNames.Contains(h) ? h : null;
    }

    /// <summary>
    /// 解析候选层（双层决策第一层，只依赖硬件能力）。与 JS candidateTiers 完全一致：
    /// cpu → ["cpu"]；gpu → 指定层（不可用抛错）；auto+显式层 → 白名单过滤 [层,cpu]；
    /// 默认链：厂商层 → swdec → d3d → cpu。
    /// </summary>
    public static List<string> CandidateTiers(HardwareCaps caps, string decodeMode = "auto", string? hwaccel = null)
    {
        if (decodeMode == "cpu") return ["cpu"];

        var normHw = NormalizeHwaccelName(hwaccel);
        if (normHw == "cpu") return ["cpu"];

        if (decodeMode == "gpu")
        {
            var name = normHw
                ?? throw new ArgumentException(
                    "decodeMode=gpu requires --hwaccel (cuda|qsv|amf|d3d|d3d11va|d3d12va|dxva2), got '" + (hwaccel ?? "") + "'");
            if (!caps.Usable.TryGetValue(name, out var ok) || !ok)
            {
                throw new InvalidOperationException(
                    $"hwaccel '{name}' is not available on this machine (vendor={caps.Vendor})");
            }
            return [name];
        }

        // auto 模式 + 显式 hwaccel（"auto" 与 "cpu" 除外）：白名单过滤，保留 cpu 兜底
        if (!string.IsNullOrEmpty(hwaccel) && !hwaccel.Equals("auto", StringComparison.OrdinalIgnoreCase))
        {
            var name = normHw;
            if (name is not null)
            {
                var usable = caps.Usable.TryGetValue(name, out var ok) && ok;
                return usable ? [name, "cpu"] : ["cpu"];
            }
            // 非法值：JS warn 后走默认链
        }

        // auto 默认链（T6）
        var vendor = PrimaryVendor(caps);
        var allowed = GpuVendorHwaccels.TryGetValue(vendor, out var a) ? a : GpuVendorHwaccels["other"];
        bool IsChainTierAvail(string t)
        {
            if (!TierWhitelisted(caps.Hwaccels, t)) return false;
            if (!caps.Usable.TryGetValue(t, out var ok) || !ok) return false;
            if (FilterSupportKey.TryGetValue(t, out var filterKey) &&
                caps.FilterSupport.TryGetValue(filterKey, out var fs) && !fs)
            {
                return false;
            }
            return true;
        }

        var list = new List<string>();
        // 1) 主厂商硬件解码层（cuda / qsv / amf）
        foreach (var t in allowed)
        {
            if (t == "d3d") continue;
            if (IsChainTierAvail(t)) list.Add(t);
        }
        // 2) swdec 层（CPU 解码 + 硬件编码）：固定在主解码层之后、d3d 与 cpu 之前
        if (allowed.Contains("d3d") && SwdecUsable(caps, vendor)) list.Add("swdec");
        // 3) d3d 解码兜底层（延后到 swdec 之后）
        if (allowed.Contains("d3d") && IsChainTierAvail("d3d")) list.Add("d3d");
        // 4) 兜底保证 swdec 仍在 cpu 之前
        if (!list.Contains("swdec") && SwdecUsable(caps, vendor)) list.Add("swdec");
        list.Add("cpu");
        return list;
    }
}
