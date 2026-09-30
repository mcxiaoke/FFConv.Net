namespace MediaCli.Transcode.Model;

/// <summary>Video stream info (subset used by the transcode domain).</summary>
public sealed class VideoInfo
{
    public string? Format { get; set; }       // codec name, e.g. h264 / hevc
    public string? Profile { get; set; }
    public string? Level { get; set; }
    public double Duration { get; set; }
    public long Bitrate { get; set; }
    public int Width { get; set; }
    public int Height { get; set; }
    public double FrameRate { get; set; }
    public string? PixelFormat { get; set; }  // ffprobe pix_fmt or mediainfo "YUV4:2:0"
    public int? BitDepth { get; set; }        // explicit bit depth (mediainfo BitDepth / bits_per_raw_sample)
    public int? StreamIndex { get; set; }     // absolute stream index in container (ffprobe index)
    public Dictionary<string, string> Tags { get; set; } = new(StringComparer.OrdinalIgnoreCase);
}

/// <summary>Audio stream info (subset used by the transcode domain).</summary>
public sealed class AudioInfo
{
    public string? Format { get; set; }       // codec name
    public string? CodecId { get; set; }      // codec_tag_string / CodecID
    public double Duration { get; set; }
    public long Bitrate { get; set; }
    public int? SampleRate { get; set; }
    public Dictionary<string, string> Tags { get; set; } = new(StringComparer.OrdinalIgnoreCase);
}

/// <summary>Subtitle stream info (subset used by the transcode domain).</summary>
public sealed class SubtitleInfo
{
    public string? Format { get; set; }       // codec_name, e.g. hdmv_pgs_subtitle
    public string? Codec { get; set; }        // codec_tag_string
    public string? Language { get; set; }
}

/// <summary>
/// Container-level media info. Port of the entry.info shape produced by
/// lib/mediainfo.js and consumed by ffmpeg_plan/ffmpeg_build/ffmpeg_run.
/// </summary>
public sealed class MediaInfo
{
    public double Duration { get; set; }
    public long Bitrate { get; set; }
    public string? Format { get; set; }       // container format name, e.g. "matroska,webm"
    public long Size { get; set; }
    public bool Lossless { get; set; }
    public VideoInfo? Video { get; set; }
    public AudioInfo? Audio { get; set; }
    public List<SubtitleInfo> Subtitles { get; set; } = [];
    public Dictionary<string, string> Tags { get; set; } = new(StringComparer.OrdinalIgnoreCase);
}

/// <summary>Music metadata read from an audio file (port of readMusicMeta result).</summary>
public sealed class MusicMeta
{
    public long? Bitrate { get; set; }
    public double? Duration { get; set; }
    public string? Codec { get; set; }
    public bool Lossless { get; set; }
    public Dictionary<string, string> Tags { get; set; } = new(StringComparer.OrdinalIgnoreCase);
}
