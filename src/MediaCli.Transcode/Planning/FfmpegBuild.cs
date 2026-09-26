using System.Text.RegularExpressions;
using MediaCli.Transcode.Hardware;
using MediaCli.Transcode.Model;
using MediaCli.Transcode.Presets;
using MediaCli.Transcode.Support;

namespace MediaCli.Transcode.Planning;

public static partial class FfmpegBuild
{
    public sealed record FilterSegments(string Pre, string Post, bool ScaleRequested);

    private static readonly HashSet<string> BitmapSubtitleFormats = new(StringComparer.Ordinal)
    {
        "hdmv_pgs_subtitle", "pgs", "dvd_subtitle", "vobsub", "dvb_subtitle", "dvb_teletext", "xsub", "arib_caption"
    };

    private static readonly string[] SubArgsMkv = ["-c:s", "copy", "-map", "0:v:0", "-map", "0:a?", "-map", "0:s?"];
    private static readonly string[] SubArgsMp4 = ["-c:s", "mov_text", "-map", "0:v:0", "-map", "0:a?", "-map", "0:s?"];
    private static readonly string[] SubArgsMp4Drop = ["-sn", "-map", "0:v:0", "-map", "0:a?"];

    private static readonly string[] MetaKeyList = ["title", "artist", "album", "albumartist", "year"];

    public static string? FlattenFFArgs(IReadOnlyList<string>[]? ffmpegArgs)
    {
        if (ffmpegArgs != null)
        {
            return string.Join(" ", ffmpegArgs.SelectMany(a => a));
        }
        return null;
    }

    internal static string TierName(HwPlan? hwPlan) => hwPlan?.Tier.Name ?? "cpu";

    private static string StripEdgeComma(string s) => RxEdgeComma().Replace(s, "");

    [GeneratedRegex("^,+|,+$")]
    private static partial Regex RxEdgeComma();

    public static FilterSegments SplitPresetFilterSegments(TempPreset tempPreset)
    {
        string text = tempPreset.Preset.PreFilters ?? "";
        string text2 = tempPreset.Preset.PostFilters ?? "";
        bool scaleRequested = false;
        string text3 = tempPreset.Filters ?? "";
        if (text3.Contains("{scaleFilter}"))
        {
            scaleRequested = true;
            int num = text3.IndexOf("{scaleFilter}", StringComparison.Ordinal);
            string text4 = StripEdgeComma(text3[..num]);
            string text5 = StripEdgeComma(text3[(num + "{scaleFilter}".Length)..]);
            if (text.Length == 0 && text4.Length > 0)
            {
                text = text4;
            }
            if (text2.Length == 0 && text5.Length > 0)
            {
                text2 = text5;
            }
        }
        else if (text3.Length > 0 && text.Length == 0)
        {
            text = text3;
        }
        return new FilterSegments(text, text2, scaleRequested);
    }

    private static Size? ResolveEffectiveSize(HwPlan? hwPlan, TranscodeEntry entry, TempPreset tempPreset)
    {
        Size? size = hwPlan?.Size;
        if (size != null && size.W > 0 && size.H > 0)
        {
            return size;
        }
        if (hwPlan?.Tier == null)
        {
            return null;
        }
        int w = entry.Info?.Video?.Width ?? 0;
        int h = entry.Info?.Video?.Height ?? 0;
        if (w <= 0 || h <= 0)
        {
            return null;
        }
        long dim = tempPreset.Dimension > 0 ? tempPreset.Dimension : entry.Preset.Dimension;
        if (dim <= 0)
        {
            return new Size(HwAccel.ToEven(w), HwAccel.ToEven(h));
        }
        Size size2 = HwAccel.CalcLongEdge(w, h, dim);
        if (size2.W <= 0 || size2.H <= 0)
        {
            return null;
        }
        return size2;
    }

    private static bool DepthAlignNeeded(TranscodeEntry entry, HwPlan? hwPlan, TempPreset tempPreset)
    {
        return HwAccel.ScaleFormatOverride(
            hwPlan?.Tier,
            HwAccel.CodecFamilyOfPreset(tempPreset.Preset),
            entry.Info?.Video?.PixelFormat ?? "",
            entry.Info?.Video?.BitDepth) != null;
    }

    internal static string BuildScaleFiltersFromPlan(TranscodeEntry entry, HwPlan? hwPlan, TempPreset tempPreset)
    {
        var (pre, post, scaleRequested) = SplitPresetFilterSegments(tempPreset);
        TierDef? tier = hwPlan?.Tier;
        double speed = HwAccel.ValidateSpeed(tempPreset.Speed);
        double fps = tempPreset.Framerate > 0.0 ? tempPreset.Framerate : 0.0;
        Size? size = ResolveEffectiveSize(hwPlan, entry, tempPreset);
        if (tier == null || size == null)
        {
            List<string> list = [];
            if (pre.Length > 0) list.Add(pre);
            if (speed != 1.0) list.Add("setpts=PTS/" + HwAccel.FormatSpeed(speed));
            if (fps > 0.0) list.Add("fps=" + HwAccelInternal.FormatNum(fps));
            if (post.Length > 0) list.Add(post);
            return string.Join(",", list);
        }
        bool hasScale = scaleRequested || tempPreset.Scaled || fps > 0.0;
        return HwAccel.BuildVideoFilters(new HwVideoFilterOptions
        {
            Tier = tier,
            Size = size,
            Speed = speed,
            Framerate = fps,
            PreFilters = pre,
            PostFilters = post,
            HasScale = hasScale,
            CodecFamily = HwAccel.CodecFamilyOfPreset(tempPreset.Preset),
            PixFmt = entry.Info?.Video?.PixelFormat ?? "",
            BitDepth = entry.Info?.Video?.BitDepth
        });
    }

    public static (IReadOnlyList<string>[] Args, Dictionary<string, object?> DebugPreset) CreateFFmpegArgs(TranscodeEntry entry, HwPlan? hwPlan = null)
    {
        TempPreset tempPreset = new(entry.Preset, entry.DstArgs ?? new DstArgs());
        List<string> inputArgs = BuildInputArgs(entry, tempPreset, hwPlan);
        bool isVideo = tempPreset.Type == "video";
        List<string> middleArgs = [];
        if (isVideo)
        {
            middleArgs.AddRange(BuildFilterArgs(entry, tempPreset, hwPlan));
            middleArgs.AddRange(BuildVideoArgs(entry, hwPlan, tempPreset));
        }
        middleArgs.AddRange(BuildAudioArgs(entry, tempPreset, hwPlan?.Caps));
        middleArgs.AddRange(BuildMetaArgs(entry, tempPreset));
        middleArgs.AddRange(BuildStreamArgs(tempPreset));
        string[] outputArgs = [entry.FileDstTemp ?? ""];
        return (Args: [inputArgs, middleArgs, outputArgs], DebugPreset: tempPreset.ToTemplateDict());
    }

    private static List<string> BuildInputArgs(TranscodeEntry entry, TempPreset tempPreset, HwPlan? hwPlan)
    {
        List<string> list = ["-hide_banner", "-n", "-v", entry.Argv.Debug ? "repeat+level+info" : "error"];
        if (tempPreset.Type == "video")
        {
            list.AddRange(["-progress", "-", "-nostats"]);
            TierDef? tier = hwPlan?.Tier;
            if (!string.IsNullOrEmpty(tier?.Hwaccel))
            {
                list.AddRange(["-hwaccel", tier.Hwaccel]);
                if (!string.IsNullOrEmpty(tier.HwFormat))
                {
                    list.AddRange(["-hwaccel_output_format", tier.HwFormat]);
                }
            }
        }
        else
        {
            list.AddRange(["-progress", "-", "-nostats"]);
        }
        if (!string.IsNullOrEmpty(tempPreset.InputArgs))
        {
            list.AddRange(tempPreset.InputArgs.Split(' ', StringSplitOptions.RemoveEmptyEntries));
        }
        list.Add("-i");
        list.Add(entry.Path);
        AppendSubtitleArgs(entry, list, tempPreset);
        return list;
    }

    private static bool IsBitmapSubtitle(SubtitleInfo sub)
    {
        string f = (sub.Format ?? "").ToLowerInvariant();
        string c = (sub.Codec ?? "").ToLowerInvariant();
        return BitmapSubtitleFormats.Contains(f) || BitmapSubtitleFormats.Contains(c);
    }

    private static void AppendSubtitleArgs(TranscodeEntry entry, List<string> inputArgs, TempPreset tempPreset)
    {
        if (tempPreset.Type != "video" && !Helper.IsVideoFile(entry.Path))
        {
            return;
        }
        string text = Path.GetExtension(entry.FileDst ?? "");
        if (text.Length == 0)
        {
            text = tempPreset.Format ?? Helper.PathExt(entry.Path);
        }
        bool isMkv = text.Contains("mkv", StringComparison.OrdinalIgnoreCase);
        if (!string.IsNullOrEmpty(entry.SelectedSubtitle))
        {
            string subCodec = isMkv ? "copy" : "mov_text";
            inputArgs.AddRange([
                "-i", entry.SelectedSubtitle,
                "-c:s", subCodec,
                "-metadata:s:s:0", "language=chi",
                "-disposition:s:0", "default",
                "-map", "0:v:0",
                "-map", "0:a?",
                "-map", "1:0?"
            ]);
        }
        else if (isMkv)
        {
            inputArgs.AddRange(SubArgsMkv);
        }
        else
        {
            List<SubtitleInfo>? list = entry.Info?.Subtitles;
            if (list != null && list.Count > 0 && list.Any(IsBitmapSubtitle))
            {
                inputArgs.AddRange(SubArgsMp4Drop);
            }
            else
            {
                inputArgs.AddRange(SubArgsMp4);
            }
        }
    }

    private static List<string> BuildFilterArgs(TranscodeEntry entry, TempPreset tempPreset, HwPlan? hwPlan)
    {
        List<string> list = [];
        var (pre, post, _) = SplitPresetFilterSegments(tempPreset);
        double speed = HwAccel.ValidateSpeed(tempPreset.Speed);
        if (tempPreset.Scaled || tempPreset.Framerate > 0.0 || pre.Length > 0 || post.Length > 0 || speed != 1.0 || DepthAlignNeeded(entry, hwPlan, tempPreset))
        {
            string scaleFilters = BuildScaleFiltersFromPlan(entry, hwPlan, tempPreset);
            string tier = TierName(hwPlan);
            if ((tier == "cpu" || tier == "swdec") && RxCudaFilter().IsMatch(scaleFilters))
            {
                scaleFilters = "hwupload_cuda," + scaleFilters;
            }
            if (scaleFilters.Length > 0)
            {
                list.Add("-vf");
                list.Add(Core.FormatArgs(scaleFilters, tempPreset.ToTemplateDict()));
            }
        }
        bool hasAudio = entry.Info?.Audio != null || !string.IsNullOrEmpty(entry.DstArgs?.SrcAudioCodec);
        string audioFilters = HwAccel.BuildAudioFilters(tempPreset.Speed);
        if (audioFilters.Length > 0 && hasAudio)
        {
            list.AddRange(["-af", audioFilters]);
        }
        return list;
    }

    [GeneratedRegex("_cuda\\b")]
    private static partial Regex RxCudaFilter();

    private static List<string> BuildVideoArgs(TranscodeEntry entry, HwPlan? hwPlan, TempPreset tempPreset)
    {
        List<string>? list = BuildVideoArgsFromPlan(entry, hwPlan, tempPreset);
        return list != null ? list.ToList() : [];
    }

    internal static List<string>? BuildVideoArgsFromPlan(TranscodeEntry entry, HwPlan? hwPlan, TempPreset tempPreset)
    {
        TierDef? tier = hwPlan?.Tier;
        if (tier == null)
        {
            return null;
        }
        string? videoCodec = tempPreset.UserArgs.VideoCodec;
        if (videoCodec == "copy")
        {
            return ["-c:v", "copy"];
        }
        string codecFamily = HwAccel.CodecFamilyOfPreset(tempPreset.Preset);
        double quality = tempPreset.VideoQuality > 0.0
            ? tempPreset.VideoQuality
            : (entry.Preset.VideoQuality > 0.0 ? entry.Preset.VideoQuality : 24.0);

        long? bitrate = tempPreset.DstVideoBitrate > 0 ? tempPreset.DstVideoBitrate : null;
        long? maxBitrate = tempPreset.DstMaxBitrate > 0 ? tempPreset.DstMaxBitrate : null;
        string pixFmt = entry.Info?.Video?.PixelFormat ?? "";

        EncoderArgs encoderArgs = new()
        {
            Quality = quality,
            Bitrate = bitrate,
            MaxBitrate = maxBitrate,
            CodecFamily = codecFamily,
            PixFmt = pixFmt,
            BitDepth = entry.Info?.Video?.BitDepth,
            ForcedEncoder = videoCodec,
            Tier = tier,
            Encoders = hwPlan?.Caps?.Encoders,
            Anime = tempPreset.Anime || (entry.DstArgs?.Anime ?? false) || entry.Preset.UserArgs.Anime
        };

        return HwAccel.BuildEncoderArgs(tier.Name, encoderArgs).ToList();
    }

    private static List<string> BuildAudioArgs(TranscodeEntry entry, TempPreset tempPreset, HardwareCaps? caps)
    {
        bool strict = entry.Argv.Strict;
        bool isSpeedChanged = HwAccel.ValidateSpeed(tempPreset.Speed) != 1.0;
        bool shouldCopy = false;

        if (tempPreset.UserArgs.AudioCopy || tempPreset.AudioCodec == "copy" || tempPreset.AudioArgs == "-c:a copy")
        {
            shouldCopy = true;
        }
        else if (FFmpegPresets.IsAudioExtract(tempPreset.Preset))
        {
            if (entry.DstArgs?.SrcAudioCodec == "aac")
            {
                shouldCopy = true;
            }
        }
        else if (Helper.IsVideoFile(entry.Path))
        {
            bool bitrateCond = tempPreset.SrcAudioBitrate > 0 && tempPreset.DstAudioBitrate + 2000 > tempPreset.SrcAudioBitrate;
            bool formatCond = Helper.IsAudioCodecCompatibleWithContainer(
                dstExt: tempPreset.Format ?? Helper.PathExt(entry.Path),
                audioCodec: entry.DstArgs?.SrcAudioCodec,
                audioCodecId: entry.Info?.Audio?.CodecId);
            shouldCopy = bitrateCond && formatCond;
        }

        if (shouldCopy && !isSpeedChanged)
        {
            return ["-c:a", "copy"];
        }

        string codecOrArgs = string.IsNullOrEmpty(tempPreset.UserArgs.AudioCodec)
            ? (tempPreset.AudioCodec.Length > 0 ? tempPreset.AudioCodec : "aac")
            : tempPreset.UserArgs.AudioCodec;

        if (!string.IsNullOrEmpty(tempPreset.AudioArgs) && string.IsNullOrEmpty(tempPreset.UserArgs.AudioCodec))
        {
            Match match = RxAudioCodec().Match(tempPreset.AudioArgs);
            if (match.Success)
            {
                codecOrArgs = match.Groups[1].Value;
            }
        }

        codecOrArgs = FallbackAudioEncoder(codecOrArgs, caps?.Encoders, strict);
        List<string> list = ["-c:a", codecOrArgs];

        long bitrate = tempPreset.UserArgs.AudioBitrate > 0
            ? tempPreset.UserArgs.AudioBitrate
            : (tempPreset.DstAudioBitrate > 0 ? tempPreset.DstAudioBitrate : tempPreset.Preset.AudioBitrate);

        if (bitrate == 0L && !string.IsNullOrEmpty(tempPreset.AudioArgs))
        {
            Match match2 = RxAudioBitrate().Match(tempPreset.AudioArgs);
            if (match2.Success)
            {
                try
                {
                    bitrate = Helper.ParseBitrate(match2.Groups[1].Value);
                }
                catch
                {
                }
            }
        }

        if (bitrate > 0)
        {
            list.AddRange(["-b:a", $"{Math.Round((double)bitrate / 1000.0)}k"]);
        }
        else
        {
            double q = tempPreset.UserArgs.AudioQuality > 0 ? tempPreset.UserArgs.AudioQuality : tempPreset.AudioQuality;
            if (q > 0.0)
            {
                list.AddRange(["-q:a", HwAccelInternal.FormatNum(q)]);
            }
        }
        return list;
    }

    [GeneratedRegex(@"-c:a(?::\d+)?\\s+(\\S+)")]
    private static partial Regex RxAudioCodec();

    [GeneratedRegex(@"-b:a\\s+(\\S+)")]
    private static partial Regex RxAudioBitrate();

    public static string FallbackAudioEncoder(string? codecOrArgs, HashSet<string>? encoders, bool strict = false)
    {
        if (string.IsNullOrEmpty(codecOrArgs) || encoders == null || encoders.Count == 0)
        {
            return codecOrArgs ?? "";
        }
        string text = codecOrArgs.Trim();
        Match match = RxAudioCodec().Match(text);
        if (match.Success)
        {
            string value = match.Groups[1].Value;
            if (value == "copy" || encoders.Contains(value) || strict)
            {
                return codecOrArgs;
            }
            return text.Replace(match.Value, match.Value.Replace(value, "aac"));
        }
        if (text == "copy" || encoders.Contains(text) || strict)
        {
            return codecOrArgs;
        }
        return "aac";
    }

    internal static bool HasMkvStatistics(TranscodeEntry entry)
    {
        if (entry == null) return false;
        string ext = Path.GetExtension(entry.Path ?? "").ToLowerInvariant();
        string fmt = (entry.Info?.Format ?? "").ToLowerInvariant();
        bool isMkvExt = ext is ".mkv" or ".mka" or ".webm";
        if (!isMkvExt && !fmt.Contains("matroska") && !fmt.Contains("webm"))
        {
            return false;
        }
        if (!CheckTags(entry.Tags) && !CheckTags(entry.Info?.Tags) && !CheckTags(entry.Info?.Video?.Tags))
        {
            return CheckTags(entry.Info?.Audio?.Tags);
        }
        return true;

        static bool CheckTags(IReadOnlyDictionary<string, string>? tags)
        {
            if (tags == null) return false;
            foreach (string key in tags.Keys)
            {
                string u = key.ToUpperInvariant();
                if (u is "_STATISTICS_TAGS" or "_STATISTICS_WRITING_APP" or "BPS"
                    || u.StartsWith("BPS-") || u == "NUMBER_OF_BYTES" || u.StartsWith("NUMBER_OF_BYTES-")
                    || u == "NUMBER_OF_FRAMES" || u.StartsWith("NUMBER_OF_FRAMES-"))
                {
                    return true;
                }
            }
            return false;
        }
    }

    private static List<string> BuildMetaArgs(TranscodeEntry entry, TempPreset tempPreset)
    {
        List<string> list = [];
        if (Helper.IsAudioFile(entry.Path))
        {
            Dictionary<string, string>? tags = entry.Tags;
            if (tags != null && tags.Count > 0 && tags.ContainsKey("title"))
            {
                foreach (var (k, v) in tags)
                {
                    if (MetaKeyList.Contains(k) && !string.IsNullOrEmpty(v))
                    {
                        string safeV = v.Replace("'", " ").Replace("\"", " ");
                        list.AddRange(["-metadata", $"{k}={safeV}"]);
                    }
                }
                if (!list.Any(a => a.Contains('=')))
                {
                    list.AddRange(["-metadata", "title=" + Path.GetFileNameWithoutExtension(entry.Name)]);
                }
                goto CheckMkv;
            }
        }

        string title = Path.GetFileNameWithoutExtension(entry.Name);
        if (title.Length == 0) title = entry.Name;
        list.AddRange(["-metadata", "title=" + title]);

    CheckMkv:
        if (HasMkvStatistics(entry))
        {
            if (tempPreset.Type == "video")
            {
                string[] videoTags = ["BPS=", "NUMBER_OF_BYTES=", "NUMBER_OF_FRAMES=", "_STATISTICS_TAGS=", "_STATISTICS_WRITING_APP=", "_STATISTICS_WRITING_DATE_UTC="];
                foreach (string t in videoTags)
                {
                    list.AddRange(["-metadata:s:v", t]);
                }
            }
            if (tempPreset.UserArgs.AudioCodec != "copy" && tempPreset.AudioCodec != "copy")
            {
                string[] audioTags = ["BPS=", "NUMBER_OF_BYTES=", "NUMBER_OF_FRAMES=", "_STATISTICS_TAGS=", "_STATISTICS_WRITING_APP=", "_STATISTICS_WRITING_DATE_UTC="];
                foreach (string t in audioTags)
                {
                    list.AddRange(["-metadata:s:a", t]);
                }
            }
        }

        foreach (var (k, v) in tempPreset.UserArgs.MetadataPairs)
        {
            list.AddRange(["-metadata", $"{k}={v}"]);
        }
        return list;
    }

    private static List<string> BuildStreamArgs(TempPreset tempPreset)
    {
        List<string> list = [];
        if (!string.IsNullOrEmpty(tempPreset.StreamArgs))
        {
            string s = RxMapMetadata().Replace(tempPreset.StreamArgs, "");
            s = RxWhitespace().Replace(s, " ").Trim();
            if (s.Length > 0)
            {
                list.AddRange(s.Split(' ', StringSplitOptions.RemoveEmptyEntries));
            }
        }
        if (!string.IsNullOrEmpty(tempPreset.OutputArgs))
        {
            list.AddRange(tempPreset.OutputArgs.Split(' ', StringSplitOptions.RemoveEmptyEntries));
        }
        return list;
    }

    [GeneratedRegex(@"-map_metadata:s:[va]\\s+\\S+")]
    private static partial Regex RxMapMetadata();

    [GeneratedRegex(@"\\s+")]
    private static partial Regex RxWhitespace();
}
