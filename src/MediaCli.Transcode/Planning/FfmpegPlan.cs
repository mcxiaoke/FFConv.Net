using System.Text.RegularExpressions;
using MediaCli.Transcode.Model;
using MediaCli.Transcode.Hardware;
using MediaCli.Transcode.Support;

namespace MediaCli.Transcode.Planning;

/// <summary>
/// Port of ffmpeg_plan.js: 纯计算模块 —— 码率/分辨率/帧率/命名等目标参数计算。
/// 不触碰文件系统、不执行进程。
/// </summary>
public static partial class FfmpegPlan
{
    /// <summary>音频码率映射表（1K=1000）。</summary>
    private static readonly (long Threshold, long Value)[] BitrateMap =
    [
        (320 * 1000, 320 * 1000),
        (256 * 1000, 256 * 1000),
        (192 * 1000, 192 * 1000),
        (128 * 1000, 128 * 1000),
        (96 * 1000, 96 * 1000),
        (64 * 1000, 64 * 1000),
        (0, 48 * 1000), // 默认值
    ];

    public static long MinNoZero(params long[] numbers)
    {
        long min = 0;
        foreach (var n in numbers)
        {
            if (n > 0 && (min == 0 || n < min)) min = n;
        }
        return min;
    }

    private static string KNum(double value) => $"{Math.Round(value / 1000)}K";

    /// <summary>
    /// 计算视频和音频码率等各种目标文件数据（calculateDstArgs 完整移植）。
    /// </summary>
    public static DstArgs CalculateDstArgs(TranscodeEntry entry)
    {
        var ep = entry.Preset;
        var info = entry.Info;
        var ivideo = info?.Video;
        var iaudio = info?.Audio;

        long srcAudioBitrate = 0;
        long dstAudioBitrate;
        long srcVideoBitrate = 0;
        long dstVideoBitrate = 0;
        long dstMaxBitrate = 0;

        int dstWidth = 0;
        int dstHeight = 0;

        var srcDuration = info is { Duration: > 0 } ? info.Duration
            : ivideo is { Duration: > 0 } ? ivideo.Duration
            : iaudio?.Duration ?? 0;

        var srcWidth = ivideo?.Width ?? 0;
        var srcHeight = ivideo?.Height ?? 0;

        var reqAudioBitrate = ep.UserArgs.AudioBitrate > 0 ? ep.UserArgs.AudioBitrate : ep.AudioBitrate;
        var reqVideoBitrate = ep.UserArgs.VideoBitrate > 0 ? ep.UserArgs.VideoBitrate : ep.VideoBitrate;

        var dstAudioQuality = ep.UserArgs.AudioQuality > 0 ? ep.UserArgs.AudioQuality : ep.AudioQuality;
        var dstVideoQuality = ep.UserArgs.VideoQuality > 0 ? ep.UserArgs.VideoQuality : ep.VideoQuality;

        // 动漫模式质量自适应（Same-VMAF 原则）：
        // AV1 基准 33→37（+4），HEVC/H264 基准 23→25（+2）
        var isAnime = ep.UserArgs.Anime;
        if (isAnime && ep.UserArgs.VideoQuality == 0 && dstVideoQuality > 0)
        {
            var fam = string.IsNullOrEmpty(ep.VideoCodecFamily) ? "h264" : ep.VideoCodecFamily!;
            var offset = fam == "av1" ? 4 : 2;
            dstVideoQuality = Math.Min(51, dstVideoQuality + offset);
        }

        var dstSpeed = ep.UserArgs.Speed > 0 ? ep.UserArgs.Speed : ep.Speed;
        var dstDimension = ep.UserArgs.Dimension > 0 ? ep.UserArgs.Dimension : ep.Dimension;

        // 只有目标长边小于原视频长边时才需要缩放
        var dstScaleNeeded = srcWidth > dstDimension || srcHeight > dstDimension;

        if (Helper.IsAudioFile(entry.Path))
        {
            // ---- 音频文件 ----
            var fileBitrate =
                entry.FormatMeta?.Bitrate > 0 ? entry.FormatMeta.Bitrate.Value
                : info is { Bitrate: > 0 } ? info.Bitrate
                : iaudio is { Bitrate: > 0 } ? iaudio.Bitrate
                : 0;
            if (fileBitrate > 0)
            {
                srcAudioBitrate = fileBitrate;
            }
            else if (entry.FormatMeta?.Lossless == true || info?.Lossless == true || Helper.IsAudioLossless(entry.Path))
            {
                // 无损音频，设置默认值
                srcAudioBitrate = srcAudioBitrate > 320 * 1000 ? srcAudioBitrate : 999 * 1000;
            }
            if (srcAudioBitrate > 0)
            {
                if (ep.SmartBitrate == true)
                {
                    dstAudioBitrate = BitrateMap.FirstOrDefault(br => srcAudioBitrate > br.Threshold).Value;
                }
                else
                {
                    dstAudioBitrate = reqAudioBitrate;
                }
            }
            else
            {
                // 无法获取音频码率（如 opus）：dstAudioBitrate=48k 可接受
                dstAudioBitrate = 48 * 1000;
            }
            // 转换后的码率不能高于源文件码率
            dstAudioBitrate = MinNoZero(dstAudioBitrate, srcAudioBitrate);
        }
        else
        {
            // ---- 视频文件 ----
            if (srcWidth > 0 && srcHeight > 0)
            {
                if (dstDimension > 0)
                {
                    var s = HwAccel.CalcLongEdge(srcWidth, srcHeight, dstDimension);
                    dstWidth = s.W;
                    dstHeight = s.H;
                }
                else
                {
                    dstWidth = HwAccel.ToEven(srcWidth);
                    dstHeight = HwAccel.ToEven(srcHeight);
                }
            }
            var bigSideSrc = (long)Math.Max(srcWidth, srcHeight);
            var dstPixels = (long)dstWidth * dstHeight;
            var fileBitrate = info?.Bitrate ?? 0;
            srcAudioBitrate = iaudio?.Bitrate ?? 0;
            // 减去音频的码率，估算为 48k
            srcVideoBitrate = ivideo is { Bitrate: > 0 } ? ivideo.Bitrate
                : fileBitrate - 48 * 1000 > 0 ? fileBitrate - 48 * 1000
                : 0;

            dstAudioBitrate = MinNoZero(srcAudioBitrate, reqAudioBitrate);
            // ── 分辨率码率缩放：像素面积幂律（主流 Bitrate Ladder 做法）──
            const double PowScale = 0.75;
            var anchorRatio = bigSideSrc > 0 && dstDimension > 0 ? (double)dstDimension / bigSideSrc : 1.0;
            var anchorPixels = (long)Math.Round(srcWidth * anchorRatio) * (long)Math.Round(srcHeight * anchorRatio);
            if (anchorPixels <= 0) anchorPixels = dstPixels;
            long ScaleBitrateByResolution(long bitrate)
            {
                if (dstPixels <= 0 || anchorPixels <= 0) return 0;
                var scale = Math.Pow((double)dstPixels / anchorPixels, PowScale);
                return (long)Math.Round(bitrate * scale);
            }
            // 仅在指定了视频目标码率时才计算；未指定保持 0（CQ/CRF 模式）
            dstVideoBitrate = reqVideoBitrate > 0
                ? MinNoZero(ScaleBitrateByResolution(reqVideoBitrate), srcVideoBitrate)
                : 0;

            // 峰值码率：用户指定 > 预设；随分辨率同样缩放；缺省 0 → buildEncoderArgs ×1.5 兜底
            var reqMaxBitrate = ep.UserArgs.MaxBitrate > 0 ? ep.UserArgs.MaxBitrate : ep.MaxBitrate;
            dstMaxBitrate = reqMaxBitrate > 0 ? ScaleBitrateByResolution(reqMaxBitrate) : 0;
        }

        // 帧率：目标帧率不大于原帧率（2% 容差覆盖 23.976 家族抖动）才生效
        var srcFrameRate = ivideo?.FrameRate ?? 0;
        var reqFrameRate = ep.UserArgs.Framerate > 0 ? ep.UserArgs.Framerate : ep.Framerate;
        var dstFrameRate = reqFrameRate < srcFrameRate * 0.98 ? reqFrameRate : 0;

        return new DstArgs
        {
            SrcAudioBitrate = srcAudioBitrate,
            SrcVideoBitrate = srcVideoBitrate,
            SrcFrameRate = srcFrameRate,
            SrcDuration = srcDuration,
            SrcWidth = srcWidth,
            SrcHeight = srcHeight,
            SrcSize = info?.Size ?? 0,
            SrcVideoCodec = ivideo?.Format,
            SrcAudioCodec = iaudio?.Format,
            SrcFormat = info?.Format,
            DstAudioBitrate = dstAudioBitrate,
            DstVideoBitrate = dstVideoBitrate,
            DstMaxBitrate = dstMaxBitrate,
            DstAudioQuality = dstAudioQuality,
            DstVideoQuality = dstVideoQuality,
            DstFrameRate = dstFrameRate,
            DstWidth = dstWidth,
            DstHeight = dstHeight,
            DstSpeed = dstSpeed,
            AudioBitScale = srcAudioBitrate > 0 ? Core.RoundNum((double)dstAudioBitrate / srcAudioBitrate) : 0,
            VideoBitScale = srcVideoBitrate > 0 ? Core.RoundNum((double)dstVideoBitrate / srcVideoBitrate) : 0,
            Dimension = dstDimension,
            Anime = isAnime,
            Scaled = dstScaleNeeded,
        };
    }

    private static readonly char[] InvalidFileNameChars =
        ['\\', '/', ':', '*', '?', '"', '<', '>', '|', '\0', '\n', '\r', '\t'];

    /// <summary>文件名安全处理（helper.filenameSafe 近似：非法字符替换为下划线）。</summary>
    private static string FilenameSafe(string name)
    {
        foreach (var c in InvalidFileNameChars)
        {
            name = name.Replace(c, '_');
        }
        return name;
    }

    /// <summary>
    /// 创建目标文件名基本名（createDstBaseName）：模板变量 = {preset} + 预设字段 + dstArgs。
    /// </summary>
    public static (string Base, string Prefix, string Suffix) CreateDstBaseName(TranscodeEntry entry)
    {
        var srcBase = Path.GetFileNameWithoutExtension(entry.Name);
        var presetDict = entry.Preset.GetType()
            .GetProperties()
            .Where(p => p.CanRead && p.Name != "UserArgs" && p.Name != "Item")
            .ToDictionary(p => p.Name, p => (object?)p.GetValue(entry.Preset), StringComparer.Ordinal);
        presetDict["preset"] = entry.Preset.Name;

        var replaceArgs = new Dictionary<string, object?>(presetDict, StringComparer.Ordinal);
        if (entry.DstArgs is not null)
        {
            foreach (var kv in entry.DstArgs.ToTemplateDict()) replaceArgs[kv.Key] = kv.Value;
        }
        // 源文件派生的命名变量。
        //
        // 存在理由：同一基名、不同容器的文件（如 Big_Buck_Bunny.mkv/.mp4/.webm）在
        // 默认后缀 `_{preset}` 下会映射到同一个目标名，先跑成功、其余被判 destination_exists。
        // {srcVideoCodec} / {srcFormat} 都不可靠——同内容的 .mkv 与 .mp4 可能同为
        // "mov,mp4,..." 容器、也可能装同一 codec，只有「源扩展名」能稳定区分。
        replaceArgs["srcExt"] = Path.GetExtension(entry.Name);                                  // ".mkv"
        replaceArgs["srcExtBare"] = Path.GetExtension(entry.Name).TrimStart('.');                // "mkv"
        replaceArgs["srcStem"] = Path.GetFileNameWithoutExtension(entry.Name);                   // "Big_Buck_Bunny"
        replaceArgs["srcName"] = entry.Name;                                                     // "Big_Buck_Bunny.mkv"
        var prefix = FilenameSafe(Core.FormatArgs(entry.Preset.Prefix ?? "", replaceArgs));
        var suffix = FilenameSafe(Core.FormatArgs(entry.Preset.Suffix ?? "", replaceArgs));
        return ($"{prefix}{srcBase}{suffix}", prefix, suffix);
    }

    /// <summary>显示媒体编码和码率信息（getEntryShowInfo）。</summary>
    public static string GetEntryShowInfo(TranscodeEntry entry)
    {
        var ia = entry.Info?.Audio;
        var iv = entry.Info?.Video;
        var isSubs = entry.Info?.Subtitles;
        var dst = entry.DstArgs;
        if (dst is null) return "";
        var ac = dst.SrcAudioCodec;
        var vc = dst.SrcVideoCodec;
        var showText = new List<string>();
        showText.Add($"sz:{Helper.HumanSize(entry.Size)}");
        showText.Add($"ts:{Helper.HumanSeconds(dst.SrcDuration)}");
        if (ia is { Duration: > 0 })
        {
            showText.Add($"a:{ac}");
            if (dst.DstAudioBitrate != dst.SrcAudioBitrate)
            {
                showText.Add($"ab:{KNum(dst.SrcAudioBitrate)}=>{KNum(dst.DstAudioBitrate)}");
            }
            else
            {
                showText.Add($"ab:{KNum(dst.SrcAudioBitrate)}");
            }
            if (dst.DstAudioQuality > 0) showText.Add($"aq:{HwAccelInternal.FormatNum(dst.DstAudioQuality)}");
        }
        if (iv is { Duration: > 0 })
        {
            showText.Add($"v:{vc}({iv.Profile}@{iv.Level})");
            if (dst.DstVideoBitrate != dst.SrcVideoBitrate)
            {
                showText.Add($"vb:{KNum(dst.SrcVideoBitrate)}=>{KNum(dst.DstVideoBitrate)}");
            }
            else
            {
                showText.Add($"vb:{KNum(dst.SrcVideoBitrate)}");
            }
            if (dst.DstFrameRate > 0 && dst.DstFrameRate != dst.SrcFrameRate)
            {
                showText.Add($"fps:{HwAccelInternal.FormatNum(dst.SrcFrameRate)}=>{HwAccelInternal.FormatNum(dst.DstFrameRate)}");
            }
            else
            {
                showText.Add($"fps:{HwAccelInternal.FormatNum(dst.SrcFrameRate)}");
            }
            if (dst.DstSpeed > 0) showText.Add($"sp:{HwAccelInternal.FormatNum(dst.DstSpeed)}");
            if (dst.SrcWidth != dst.DstWidth || dst.SrcHeight != dst.DstHeight)
            {
                showText.Add($"{dst.SrcWidth}x{dst.SrcHeight}=>{dst.DstWidth}x{dst.DstHeight}");
            }
            else
            {
                showText.Add($"{dst.SrcWidth}x{dst.SrcHeight}");
            }
        }
        if (isSubs is { Count: > 0 })
        {
            showText.Add(string.Join("|", isSubs.Select(s => $"{s.Format}-{s.Language}")));
        }
        return string.Join(",", showText);
    }

    /// <summary>
    /// 从字幕文件路径列表中优先选择中文字幕（selectPreferredSubtitle）。
    /// 拉丁关键词按词元精确匹配，CJK 关键词按词元 includes 匹配；无命中返回第一个。
    /// </summary>
    public static string? SelectPreferredSubtitle(IReadOnlyList<string> subtitles)
    {
        if (subtitles.Count == 0) return null;
        if (subtitles.Count == 1) return subtitles[0];
        string[] chineseKeywords = ["chinese", "chs", "zh", "zhcn", "chi", "gb", "简体", "简中"];
        string[] cjkKeywords = ["简体", "简中"];
        foreach (var sub in subtitles)
        {
            var lowerSub = sub.ToLowerInvariant();
            var tokens = RxTokenSplit().Split(lowerSub);
            var hit = tokens.Any(token =>
                chineseKeywords.Contains(token) ||
                cjkKeywords.Any(kw => token.Contains(kw, StringComparison.Ordinal)));
            if (hit) return sub;
        }
        return subtitles[0];
    }

    // 非字母数字（含中文保留）切词：JS /[^a-z0-9\u4e00-\u9fff]+/u
    [GeneratedRegex(@"[^a-z0-9\u4e00-\u9fff]+")]
    private static partial Regex RxTokenSplit();
}

/// <summary>数字格式化辅助（JS 模板 ${num} 语义：整数不带小数点）。</summary>
public static class HwAccelInternal
{
    public static string FormatNum(double v)
        => v == Math.Floor(v)
            ? ((long)v).ToString(System.Globalization.CultureInfo.InvariantCulture)
            : v.ToString(System.Globalization.CultureInfo.InvariantCulture);
}
