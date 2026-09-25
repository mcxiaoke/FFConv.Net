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
                foreach (var stream in streams.EnumerateArray())
                {
                    var type = stream.TryGetProperty("codec_type", out var ct) ? ct.GetString() : null;
                    switch (type)
                    {
                        case "video" when info.Video is null:
                            info.Video = new VideoInfo
                            {
                                Format = GetString(stream, "codec_name"),
                                Profile = GetString(stream, "profile"),
                                Level = stream.TryGetProperty("level", out var lv) ? lv.GetRawText() : null,
                                Duration = GetDouble(stream, "duration"),
                                Bitrate = GetLong(stream, "bit_rate"),
                                Width = stream.TryGetProperty("width", out var w) ? w.GetInt32() : 0,
                                Height = stream.TryGetProperty("height", out var h) ? h.GetInt32() : 0,
                                FrameRate = ParseRatio(GetString(stream, "avg_frame_rate")),
                                PixelFormat = GetString(stream, "pix_fmt"),
                                BitDepth = stream.TryGetProperty("bits_per_raw_sample", out var bd)
                                    ? int.TryParse(bd.ValueKind == JsonValueKind.String ? bd.GetString() : bd.GetRawText(), out var bdi) && bdi > 0 ? bdi : null
                                    : null,
                                Tags = ParseTags(stream),
                            };
                            // format 层没有 duration 时用视频流时长兜底
                            if (info.Duration <= 0) info.Duration = info.Video.Duration;
                            if (info.Bitrate <= 0) info.Bitrate = info.Video.Bitrate;
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
            }
            return info;
        }
        catch (JsonException)
        {
            return null;
        }
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
