using System.Text.RegularExpressions;
using System.Security.Cryptography;
using System.Text;

namespace MediaCli.Transcode.Support;

/// <summary>
/// Port of the lib/helper.js helpers consumed by src/transcode (media type
/// detection, bitrate parsing, human formatting, path helpers, textHash).
/// </summary>
public static partial class Helper
{
    public static readonly string[] VideoFormats =
    [
        ".mp4", ".mov", ".wmv", ".avi", ".mkv", ".m4v", ".ts", ".flv",
        ".webm", ".rmvb", ".rm", ".vob", ".mpg",
    ];

    private static readonly string[] AudioNormal = [".aac", ".m4a", ".mp3", ".wma"];
    private static readonly string[] AudioLossless = [".ape", ".flac", ".wav", ".tta", ".dts", ".tak"];
    public static readonly string[] AudioFormats = [.. AudioNormal, .. AudioLossless];

    /// <summary>MP4-family incompatible audio codecs (lib/helper.js MP4_INCOMPATIBLE_AUDIO_CODECS).</summary>
    public static readonly string[] Mp4IncompatibleAudioCodecs =
    [
        "cook", "real_144", "real_288", "ralf", "sipr", "realaudio",
        "atrac3", "adpcm_ima_qt", "ima4", "vorbis",
    ];

    public static string PathExt(string filename)
    {
        var ext = Path.GetExtension(filename);
        return ext.ToLowerInvariant();
    }

    public static bool IsVideoFile(string filename) => VideoFormats.Contains(PathExt(filename));
    public static bool IsAudioFile(string filename) => AudioFormats.Contains(PathExt(filename));
    public static bool IsAudioLossless(string filename) => AudioLossless.Contains(PathExt(filename));
    /// <summary>ffmpeg 领域的媒体文件 = 视频 ∪ 音频（JS MEDIA_FORMATS 的转码相关子集）。</summary>
    public static bool IsMediaFile2(string filename) => IsVideoFile(filename) || IsAudioFile(filename);

    /// <summary>Port of helper.isAudioCodecCompatibleWithContainer.</summary>
    public static bool IsAudioCodecCompatibleWithContainer(string? audioCodec, string? dstExt, string? audioCodecId = null)
    {
        if (string.IsNullOrEmpty(audioCodec) && string.IsNullOrEmpty(audioCodecId)) return true;
        var codec = (audioCodec ?? "").ToLowerInvariant();
        var codecId = (audioCodecId ?? "").ToLowerInvariant();
        var ext = (dstExt ?? "").ToLowerInvariant().TrimStart('.');
        if (ext == "webm")
        {
            string[] webmAudio = ["opus", "vorbis", "libopus", "libvorbis"];
            return webmAudio.Contains(codec) || webmAudio.Contains(codecId);
        }
        string[] mp4Family = ["mp4", "m4v", "mov", "m4a"];
        if (!mp4Family.Contains(ext)) return true;
        return !Mp4IncompatibleAudioCodecs.Contains(codec) && !Mp4IncompatibleAudioCodecs.Contains(codecId);
    }

    /// <summary>
    /// Port of helper.parseBitrate. Accepts a bare bps number or a string with
    /// k/m/g unit (1000-based, e.g. "233k", "3M", "1.5g"). Returns bps.
    /// </summary>
    public static long ParseBitrate(object? value)
    {
        switch (value)
        {
            case null:
                throw new ArgumentException("invalid bitrate: null");
            case double d:
                if (!double.IsFinite(d) || d < 0) throw new ArgumentException($"invalid bitrate: {d}");
                return (long)Math.Round(d);
            case int or long or float or short or byte:
                var l = Convert.ToDouble(value);
                if (!double.IsFinite(l) || l < 0) throw new ArgumentException($"invalid bitrate: {value}");
                return (long)Math.Round(l);
        }
        var s = (value.ToString() ?? "").Trim().ToLowerInvariant();
        if (s.Length == 0) throw new ArgumentException("invalid bitrate: \"\"");
        var m = RxBitrate();
        var match = m.Match(s);
        if (!match.Success)
            throw new ArgumentException(
                $"invalid bitrate: \"{value}\" (expected e.g. \"2000000\", \"233k\", \"3M\", \"1.5g\")");
        var num = double.Parse(match.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture);
        var mult = match.Groups[2].Value switch
        {
            "k" => 1000L,
            "m" => 1000L * 1000,
            "g" => 1000L * 1000 * 1000,
            _ => 1L,
        };
        return (long)Math.Round(num * mult);
    }

    [GeneratedRegex(@"^(\d+(?:\.\d+)?)\s*([kmg]?)\s*$")]
    private static partial Regex RxBitrate();

    /// <summary>Port of helper.humanSize (SI 1000-based, 2 decimals).</summary>
    public static string HumanSize(long bytes, int dp = 2)
    {
        const int thresh = 1000;
        if (Math.Abs(bytes) < thresh) return $"{bytes} B";
        string[] units = ["kB", "MB", "GB", "TB", "PB"];
        double b = bytes;
        var u = -1;
        var r = Math.Pow(10, dp);
        do
        {
            b /= thresh;
            u++;
        } while (Math.Round(Math.Abs(b) * r) / r >= thresh && u < units.Length - 1);
        return b.ToString($"F{dp}") + units[u];
    }

    /// <summary>Port of helper.humanSeconds / humanDuration.</summary>
    public static string HumanSeconds(double seconds, int dp = 0)
    {
        var ms = seconds * 1000.0;
        var abs = Math.Abs(ms);
        if (abs >= 365 * 24 * 3600_000.0) return $"{ms / (365 * 24 * 3600_000.0):F1}y";
        if (abs >= 24 * 3600_000.0) return $"{ms / (24 * 3600_000.0):F1}d";
        if (abs >= 3600_000.0) return $"{ms / 3600_000.0:F1}h";
        if (abs >= 60_000.0) return $"{ms / 60_000.0:F1}m";
        if (abs >= 1000.0)
        {
            var s = ms / 1000.0;
            return dp > 0 ? s.ToString("F" + dp, System.Globalization.CultureInfo.InvariantCulture) + "s" : $"{Math.Round(s)}s";
        }
        return $"{Math.Round(ms)}ms";
    }

    /// <summary>Port of helper.pathShort: shorten long paths from the head with "~".</summary>
    public static string PathShort(string ps, int width = 50)
    {
        var s = Path.GetFullPath(ps);
        return s.Length < width ? s : "~" + s[^width..];
    }

    /// <summary>
    /// Deterministic 32-bit hash for temp-file naming (stands in for helper.textHash/xxHash32).
    /// Any stable hash works: the value only needs to be reproducible within a run.
    /// </summary>
    public static string TextHash(string text)
    {
        var bytes = MD5.HashData(Encoding.UTF8.GetBytes(text));
        return Convert.ToHexString(bytes)[..8].ToLowerInvariant();
    }
}
