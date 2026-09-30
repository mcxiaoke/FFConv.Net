using System.Text.Json;
using MediaCli.Transcode.Bin;
using MediaCli.Transcode.Model;

namespace MediaCli.Transcode.MediaProbe;

/// <summary>
/// ffprobe 媒体信息提供者。JS 端由 lib/mediainfo.js（mediainfo/ffprobe 双路径）提供，
/// C# 端统一走 ffprobe JSON 输出，填充 TranscodeEntry 消费的 MediaInfo 形状。
/// </summary>
public static class FfprobeMediaInfo
{
    public static MediaInfo? GetMediaInfo(string path, string? ffprobePath = null)
    {
        var bin = ffprobePath ?? FfmpegBin.ResolveFFprobeBinary();
        if (string.IsNullOrEmpty(bin) || !File.Exists(path)) return null;
        var (code, stdout, _) = FfmpegBin.RunCapture(bin,
        [
            "-v", "error", "-print_format", "json", "-show_format", "-show_streams", path,
        ], timeoutMs: 30_000);
        if (code != 0 || stdout.Length == 0) return null;
        try
        {
            var doc = JsonDocument.Parse(stdout);
            var root = doc.RootElement;
            var info = new MediaInfo();
            if (root.TryGetProperty("format", out var fmt))
            {
                info.Duration = GetDouble(fmt, "duration");
                info.Bitrate = GetLong(fmt, "bit_rate");
                info.Format = fmt.TryGetProperty("format_name", out var fn) ? fn.GetString() : null;
                info.Size = GetLong(fmt, "size");
                info.Tags = ParseTags(fmt);
            }
            if (root.TryGetProperty("streams", out var streams) && streams.ValueKind == JsonValueKind.Array)
            {
                var videoCandidates = new List<(JsonElement Stream, int CoverRank)>();
                foreach (var stream in streams.EnumerateArray())
                {
                    var type = stream.TryGetProperty("codec_type", out var ct) ? ct.GetString() : null;
                    switch (type)
                    {
                        case "video":
                            var codecName = GetString(stream, "codec_name") ?? "";
                            var isAttachedPic = stream.TryGetProperty("disposition", out var disp)
                                && disp.TryGetProperty("attached_pic", out var ap)
                                && ap.ValueKind == JsonValueKind.Number && ap.GetInt32() == 1;
                            var nbFrames = stream.TryGetProperty("nb_frames", out var nbf)
                                ? long.TryParse(nbf.ValueKind == JsonValueKind.String ? nbf.GetString() : nbf.GetRawText(), out var n) ? n : 0
                                : 0;
                            var w = stream.TryGetProperty("width", out var we) ? we.GetInt32() : 0;
                            var rank = isAttachedPic ? 2 : LooksLikeCoverArt(codecName, nbFrames, w) ? 1 : 0;
                            videoCandidates.Add((stream, rank));
                            break;
                        case "audio" when info.Audio is null:
                            info.Audio = new AudioInfo
                            {
                                Format = GetString(stream, "codec_name"),
                                CodecId = GetString(stream, "codec_tag_string"),
                                Duration = GetDouble(stream, "duration"),
                                Bitrate = GetLong(stream, "bit_rate"),
                                SampleRate = stream.TryGetProperty("sample_rate", out var sr)
                                    ? int.TryParse(sr.GetRawText(), out var sri) ? sri : null
                                    : null,
                                Tags = ParseTags(stream),
                            };
                            break;
                        case "subtitle":
                            info.Subtitles.Add(new SubtitleInfo
                            {
                                Format = GetString(stream, "codec_name"),
                                Codec = GetString(stream, "codec_tag_string"),
                                Language = ParseTags(stream).TryGetValue("language", out var lang) ? lang : null,
                            });
                            break;
                    }
                }

                // 挑选主视频流：分三档判定（0=正常视频优先，1=疑似封面/静帧次之，2=确证封面直接排除）
                var picked = videoCandidates.FirstOrDefault(c => c.CoverRank == 0).Stream;
                if (picked.ValueKind == JsonValueKind.Undefined)
                {
                    picked = videoCandidates.FirstOrDefault(c => c.CoverRank == 1).Stream;
                }

                if (picked.ValueKind != JsonValueKind.Undefined)
                {
                    var codedW = picked.TryGetProperty("width", out var wEl) ? wEl.GetInt32() : 0;
                    var codedH = picked.TryGetProperty("height", out var hEl) ? hEl.GetInt32() : 0;
                    var dispW = codedW;
                    var sar = ParseRatio(GetString(picked, "sample_aspect_ratio"));
                    if (sar > 0 && Math.Abs(sar - 1.0) > 1e-4 && codedW > 0)
                    {
                        dispW = (int)Math.Round(codedW * sar);
                    }

                    int? streamIdx = picked.TryGetProperty("index", out var idxEl) && idxEl.TryGetInt32(out var idxVal)
                        ? idxVal : null;

                    var rFps = ParseRatio(GetString(picked, "r_frame_rate"));
                    var avgFps = ParseRatio(GetString(picked, "avg_frame_rate"));
                    var fps = NormalizeFrameRate(rFps, avgFps);

                    info.Video = new VideoInfo
                    {
                        Format = GetString(picked, "codec_name"),
                        Profile = GetString(picked, "profile"),
                        Level = picked.TryGetProperty("level", out var lv) ? lv.GetRawText() : null,
                        Duration = GetDouble(picked, "duration"),
                        Bitrate = GetLong(picked, "bit_rate"),
                        Width = dispW > 0 ? dispW : codedW,
                        Height = codedH,
                        FrameRate = fps,
                        PixelFormat = GetString(picked, "pix_fmt"),
                        BitDepth = picked.TryGetProperty("bits_per_raw_sample", out var bd)
                            ? int.TryParse(bd.ValueKind == JsonValueKind.String ? bd.GetString() : bd.GetRawText(), out var bdi) && bdi > 0 ? bdi : null
                            : null,
                        StreamIndex = streamIdx,
                        Tags = ParseTags(picked),
                    };

                    // format 层没有 duration 时用视频流时长兜底
                    if (info.Duration <= 0) info.Duration = info.Video.Duration;
                    if (info.Bitrate <= 0) info.Bitrate = info.Video.Bitrate;
                }
            }
            return info;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static readonly HashSet<string> StillImageCodecs = new(StringComparer.OrdinalIgnoreCase)
    {
        "mjpeg", "png", "bmp", "webp", "tiff", "gif", "jpeg", "av1_image"
    };

    private static bool LooksLikeCoverArt(string codec, long frames, int width)
    {
        var c = codec.ToLowerInvariant().Trim();
        if (c.StartsWith("v_")) c = c[2..];
        if (!StillImageCodecs.Contains(c) && !c.Contains("mpeg4/iso/sp")) return false;
        if (frames > 1) return false;
        if (width > 1024) return false;
        return true;
    }

    private static double NormalizeFrameRate(double rFps, double avgFps)
    {
        bool IsSane(double v) => v >= 1.0 && v <= 240.0;
        if (IsSane(rFps) && IsSane(avgFps) && Math.Abs(rFps - avgFps) / avgFps <= 0.05) return rFps;
        if (IsSane(avgFps)) return avgFps;
        if (IsSane(rFps)) return rFps;
        return 0;
    }

    private static string? GetString(JsonElement el, string name)
        => el.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    private static double GetDouble(JsonElement el, string name)
        => el.TryGetProperty(name, out var v) &&
           double.TryParse(v.ValueKind == JsonValueKind.String ? v.GetString() : v.GetRawText(),
               System.Globalization.CultureInfo.InvariantCulture, out var d) ? d : 0;

    private static long GetLong(JsonElement el, string name)
        => el.TryGetProperty(name, out var v) &&
           long.TryParse(v.ValueKind == JsonValueKind.String ? v.GetString() : v.GetRawText(), out var l) ? l : 0;

    private static double ParseRatio(string? ratio)
    {
        if (string.IsNullOrEmpty(ratio)) return 0;
        var parts = ratio.Split('/');
        if (parts.Length == 2 &&
            double.TryParse(parts[0], System.Globalization.CultureInfo.InvariantCulture, out var num) &&
            double.TryParse(parts[1], System.Globalization.CultureInfo.InvariantCulture, out var den) &&
            den != 0)
        {
            return num / den;
        }
        return double.TryParse(ratio, System.Globalization.CultureInfo.InvariantCulture, out var single) ? single : 0;
    }

    private static Dictionary<string, string> ParseTags(JsonElement el)
    {
        var tags = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (el.TryGetProperty("tags", out var t) && t.ValueKind == JsonValueKind.Object)
        {
            foreach (var p in t.EnumerateObject())
            {
                if (p.Value.ValueKind == JsonValueKind.String)
                {
                    tags[p.Name] = p.Value.GetString() ?? "";
                }
            }
        }
        return tags;
    }
}
