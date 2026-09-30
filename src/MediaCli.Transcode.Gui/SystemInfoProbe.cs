using System.Diagnostics;
using MediaCli.Transcode.Hardware;
using MediaCli.Transcode.Model;

namespace MediaCli.Transcode.Gui;

/// <summary>供状态栏显示的机器与工具链信息。</summary>
public sealed record SystemInfo(
    string Cpu,
    string Gpu,
    string FfmpegVersion,
    string Vendor,
    IReadOnlyList<string> UsableTiers,
    IReadOnlyList<string> AllGpus,
    string? FfmpegPath = null,
    string? FfprobePath = null,
    string? MediainfoPath = null)
{
    /// <summary>GPU 适配器数量。</summary>
    public int GpuCount => AllGpus.Count;

    /// <summary>
    /// 状态栏单行文本（CPU · ffmpeg · 硬件层）。保持紧凑，不放 GPU，GPU 与完整环境信息放日志区。
    /// </summary>
    public string ToStatusLine()
    {
        var tiers = UsableTiers.Count > 0 ? string.Join("/", UsableTiers) : "无（cpu）";
        return $"CPU {ShortCpu(Cpu)}   ·   ffmpeg {FfmpegVersion}   ·   硬件层 {tiers}";
    }

    /// <summary>完整信息（悬停提示用，不做任何截断，保证细节不丢）。</summary>
    public string ToFullText()
    {
        var tiers = UsableTiers.Count > 0 ? string.Join("、", UsableTiers) : "无（将走 cpu）";
        var gpuLine = GpuCount > 1 ? string.Join("、", AllGpus) : Gpu;
        var list = new List<string>
        {
            $"CPU：{Cpu}",
            $"GPU：{gpuLine}",
            $"ffmpeg：{FfmpegVersion}",
        };
        if (!string.IsNullOrEmpty(FfmpegPath)) list.Add($"ffmpeg 路径：{FfmpegPath}");
        if (!string.IsNullOrEmpty(FfprobePath)) list.Add($"ffprobe 路径：{FfprobePath}");
        if (!string.IsNullOrEmpty(MediainfoPath)) list.Add($"mediainfo 路径：{MediainfoPath}");
        list.Add($"厂商：{Vendor}");
        list.Add($"可用硬件层：{tiers}");
        return string.Join(Environment.NewLine, list);
    }

    /// <summary>
    /// 去掉商标符号并压缩空白，缩短 CPU 名以便放进状态栏。
    /// public 而非 internal：测试需要验证压缩规则。
    /// </summary>
    public static string ShortCpu(string cpu) =>
        CollapseStatic(cpu
            .Replace("(R)", "", StringComparison.OrdinalIgnoreCase)
            .Replace("(TM)", "", StringComparison.OrdinalIgnoreCase)
            .Replace("CPU ", ""));

    private static string CollapseStatic(string s) =>
        string.Join(' ', s.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)).Trim();
}

/// <summary>
/// 机器与工具链信息探测（需求：状态栏显示 CPU / GPU / ffmpeg 版本）。
///
/// 复用 core 已有的 <see cref="HwDetect"/> 结果，而不是另起一套探测：
/// core 的探测包含 encoders/hwaccels/filters 且带进程内缓存，
/// 再探测一次既浪费（首次约 2–3 秒）又可能与实际执行用的结论不一致。
/// </summary>
public static class SystemInfoProbe
{
    /// <summary>CPU 型号。读注册表，不启子进程。</summary>
    public static string CpuName()
    {
        try
        {
            using var key = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(
                @"HARDWARE\DESCRIPTION\System\CentralProcessor\0");
            var name = key?.GetValue("ProcessorNameString") as string;
            if (!string.IsNullOrWhiteSpace(name)) return Collapse(name);
        }
        catch
        {
            // 注册表不可读（权限/平台差异）时退化为环境变量
        }

        var env = Environment.GetEnvironmentVariable("PROCESSOR_IDENTIFIER");
        return string.IsNullOrWhiteSpace(env) ? "未知" : Collapse(env);
    }

    /// <summary>基于 core 的硬件探测结果组装信息（探测已在别处完成并缓存）。</summary>
    public static SystemInfo FromCaps(HardwareCaps caps, string? ffmpegPath = null, string? ffprobePath = null, string? mediainfoPath = null)
    {
        // Gpu 只放主适配器：状态栏宽度有限，全量列举会把 ffmpeg 版本挤出去。
        // 完整清单放进 AllGpus，由悬停提示展示。
        var allGpus = caps.Gpus.Select(g => g.Model).ToList();
        var primaryGpu = allGpus.Count > 0 ? allGpus[0] : "未识别";

        var tiers = caps.Usable
            .Where(kv => kv.Value && kv.Key != "cpu")
            .Select(kv => kv.Key)
            .ToList();

        var version = string.IsNullOrWhiteSpace(caps.Version) ? "未知" : ShortVersion(caps.Version);
        var fPath = ffmpegPath ?? caps.FFmpegPath;
        var pPath = ffprobePath ?? MediaCli.Transcode.Bin.FfmpegBin.ResolveFFprobeBinary(fPath);
        var mPath = mediainfoPath ?? MediaCli.Transcode.Bin.FfmpegBin.ResolveMediaInfoBinary(fPath);

        return new SystemInfo(CpuName(), primaryGpu, version, caps.Vendor, tiers, allGpus, fPath, pPath, mPath);
    }

    /// <summary>不依赖 core 探测的退化信息（探测失败时仍能给出可用内容）。</summary>
    public static SystemInfo Fallback(string? ffmpegPath)
    {
        var version = "未知";
        if (!string.IsNullOrEmpty(ffmpegPath))
        {
            var (code, stdout, _) = MediaCli.Transcode.Bin.FfmpegBin.RunCapture(
                ffmpegPath, ["-hide_banner", "-version"], timeoutMs: 8000);
            if (code == 0 && !string.IsNullOrWhiteSpace(stdout))
            {
                version = ShortVersion(stdout.Split('\n')[0]);
            }
        }
        var pPath = MediaCli.Transcode.Bin.FfmpegBin.ResolveFFprobeBinary(ffmpegPath);
        var mPath = MediaCli.Transcode.Bin.FfmpegBin.ResolveMediaInfoBinary(ffmpegPath);
        return new SystemInfo(CpuName(), "未识别", version, "unknown", [], [], ffmpegPath, pPath, mPath);
    }

    /// <summary>
    /// 把 <c>ffmpeg version 8.1.2-full_build-www.gyan.dev Copyright (c) ...</c>
    /// 压成 <c>8.1.2-full_build</c>，避免状态栏被版权声明挤满。
    ///
    /// 压缩规则：只裁掉<b>末尾的厂商域名段</b>（含 '.' 的段，如 www.gyan.dev），
    /// 其余原样保留。不能简单地"只留前两段"——git-describe 形态的版本
    /// （<c>n-126733-g1a2b3c</c>）本身就是完整版本号，截断会丢失信息。
    ///
    /// public 而非 internal：测试需要覆盖各种版本串形态。
    /// </summary>
    public static string ShortVersion(string raw)
    {
        var s = Collapse(raw);
        const string prefix = "ffmpeg version ";
        if (s.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) s = s[prefix.Length..];
        var idx = s.IndexOf(" Copyright", StringComparison.OrdinalIgnoreCase);
        if (idx > 0) s = s[..idx];

        // 仅当最后一段像域名（含点）时才视为厂商后缀并裁掉
        var parts = s.Split('-');
        if (parts.Length > 1 && parts[^1].Contains('.'))
        {
            s = string.Join('-', parts[..^1]);
        }
        return s.Trim();
    }

    private static string Collapse(string s) =>
        string.Join(' ', s.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)).Trim();

    /// <summary>
    /// 后台探测并回调（不阻塞 UI）。失败时回调退化信息，保证状态栏始终有内容。
    /// </summary>
    public static void ProbeAsync(string? ffmpegPath, Action<SystemInfo> onDone, Action<string>? onError = null)
    {
        Task.Run(() =>
        {
            try
            {
                var caps = HwDetect.DetectHardwareCapabilities(ffmpegPath);
                onDone(FromCaps(caps));
            }
            catch (Exception ex)
            {
                onError?.Invoke(ex.Message);
                onDone(Fallback(ffmpegPath));
            }
        });
    }
}
