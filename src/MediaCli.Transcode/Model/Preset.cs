namespace MediaCli.Transcode.Model;

/// <summary>
/// Port of FFmpegPreset.userArgs: per-run CLI overrides (highest priority).
/// 0 / null / false means "not provided".
/// </summary>
public sealed class PresetUserArgs
{
    public long VideoBitrate { get; set; }
    public long VideoQuality { get; set; }
    public long AudioBitrate { get; set; }
    public long AudioQuality { get; set; }
    public long Dimension { get; set; }
    public double Speed { get; set; }
    public double Framerate { get; set; }
    public bool AudioCopy { get; set; }
    public bool VideoCopy { get; set; }
    public bool Anime { get; set; }
    public string? VideoCodec { get; set; }
    public string? AudioCodec { get; set; }
    public long MaxBitrate { get; set; }
    /// <summary>Metadata pairs from --metadata / ffargs md= (appended after auto metadata).</summary>
    public List<KeyValuePair<string, string>> MetadataPairs { get; set; } = [];
}

/// <summary>
/// Port of FFmpegPreset (ffmpeg_presets.js). Bitrate fields are normalized to
/// bps longs at construction (normalizeBitrate/parseBitrate semantics).
/// </summary>
public sealed class FFmpegPreset
{
    public string Name { get; set; } = "";
    public string? Format { get; set; }
    public string? Type { get; set; }              // "video" | "audio"
    public string? Prefix { get; set; }
    public string? Suffix { get; set; }
    public string? VideoCodecFamily { get; set; }  // h264 | hevc | av1 | vp9
    public string AudioCodec { get; set; } = "aac";
    public string? InputArgs { get; set; }
    public string? StreamArgs { get; set; }
    public string? OutputArgs { get; set; }
    public string? Filters { get; set; }
    public string? PreFilters { get; set; }        // pre_filters
    public string? PostFilters { get; set; }       // post_filters
    public string? Output { get; set; }
    public long VideoBitrate { get; set; }         // bps (normalized)
    public long MaxBitrate { get; set; }           // bps (normalized)
    public double VideoQuality { get; set; }
    public long AudioBitrate { get; set; }         // bps (normalized)
    public double AudioQuality { get; set; }
    public long Dimension { get; set; }
    public double Speed { get; set; } = 1;
    public double Framerate { get; set; }
    public bool? SmartBitrate { get; set; }
    /// <summary>Legacy string args (ffmpeg_presets.js audioArgs/videoArgs consumers).</summary>
    public string? AudioArgs { get; set; }
    public PresetUserArgs UserArgs { get; set; } = new();

    public FFmpegPreset Clone()
    {
        var clone = (FFmpegPreset)MemberwiseClone();
        clone.UserArgs = new PresetUserArgs
        {
            VideoBitrate = UserArgs.VideoBitrate,
            VideoQuality = UserArgs.VideoQuality,
            AudioBitrate = UserArgs.AudioBitrate,
            AudioQuality = UserArgs.AudioQuality,
            Dimension = UserArgs.Dimension,
            Speed = UserArgs.Speed,
            Framerate = UserArgs.Framerate,
            AudioCopy = UserArgs.AudioCopy,
            VideoCopy = UserArgs.VideoCopy,
            Anime = UserArgs.Anime,
            VideoCodec = UserArgs.VideoCodec,
            AudioCodec = UserArgs.AudioCodec,
            MaxBitrate = UserArgs.MaxBitrate,
            MetadataPairs = [.. UserArgs.MetadataPairs],
        };
        return clone;
    }
}

/// <summary>
/// Port of ffmpeg_plan.js calculateDstArgs output (entry.dstArgs).
/// All target parameters computed per file.
/// </summary>
public sealed class DstArgs
{
    // Source parameters
    public long SrcAudioBitrate { get; init; }
    public long SrcVideoBitrate { get; init; }
    public double SrcFrameRate { get; init; }
    public double SrcDuration { get; init; }
    public int SrcWidth { get; init; }
    public int SrcHeight { get; init; }
    public long SrcSize { get; init; }
    public string? SrcVideoCodec { get; init; }
    public string? SrcAudioCodec { get; init; }
    public string? SrcFormat { get; init; }

    // Computed target parameters
    public long DstAudioBitrate { get; init; }
    public long DstVideoBitrate { get; init; }
    public long DstMaxBitrate { get; init; }
    public double DstAudioQuality { get; init; }
    public double DstVideoQuality { get; init; }
    public double DstFrameRate { get; init; }
    public int DstWidth { get; init; }
    public int DstHeight { get; init; }
    public double DstSpeed { get; init; }
    public double AudioBitScale { get; init; }
    public double VideoBitScale { get; init; }

    // Preset field overrides (same names as preset fields, JS spread semantics)
    public long VideoBitrate => DstVideoBitrate;
    public long AudioBitrate => DstAudioBitrate;
    public double VideoQuality => DstVideoQuality;
    public double AudioQuality => DstAudioQuality;
    public double Framerate => DstFrameRate;
    public long Dimension { get; init; }
    public double Speed => DstSpeed;
    public bool Anime { get; init; }

    public bool Scaled { get; init; }

    public string VideoBitrateK => $"{Math.Round((double)DstVideoBitrate / 1000)}K";
    public string AudioBitrateK => $"{Math.Round((double)DstAudioBitrate / 1000)}K";

    /// <summary>Template replacement dict for formatArgs (file naming etc.).</summary>
    public Dictionary<string, object?> ToTemplateDict() => new(StringComparer.Ordinal)
    {
        ["srcAudioBitrate"] = SrcAudioBitrate,
        ["srcVideoBitrate"] = SrcVideoBitrate,
        ["srcFrameRate"] = SrcFrameRate,
        ["srcDuration"] = SrcDuration,
        ["srcWidth"] = SrcWidth,
        ["srcHeight"] = SrcHeight,
        ["srcSize"] = SrcSize,
        ["srcVideoCodec"] = SrcVideoCodec,
        ["srcAudioCodec"] = SrcAudioCodec,
        ["srcFormat"] = SrcFormat,
        ["dstAudioBitrate"] = DstAudioBitrate,
        ["dstVideoBitrate"] = DstVideoBitrate,
        ["dstMaxBitrate"] = DstMaxBitrate,
        ["dstAudioQuality"] = DstAudioQuality,
        ["dstVideoQuality"] = DstVideoQuality,
        ["dstFrameRate"] = DstFrameRate,
        ["dstWidth"] = DstWidth,
        ["dstHeight"] = DstHeight,
        ["dstSpeed"] = DstSpeed,
        ["audioBitScale"] = AudioBitScale,
        ["videoBitScale"] = VideoBitScale,
        ["videoBitrate"] = VideoBitrate,
        ["videoBitrateK"] = VideoBitrateK,
        ["videoQuality"] = VideoQuality,
        ["audioBitrate"] = AudioBitrate,
        ["audioBitrateK"] = AudioBitrateK,
        ["audioQuality"] = AudioQuality,
        ["framerate"] = Framerate,
        ["dimension"] = Dimension,
        ["speed"] = Speed,
        ["anime"] = Anime,
        ["scaled"] = Scaled,
    };
}

/// <summary>
/// Port of the JS merged view `tempPreset = { ...entry.preset, ...entry.dstArgs }`
/// inside createFFmpegArgs. Preset fields are overlaid by dstArgs fields with the
/// same names (videoBitrate/videoQuality/audioBitrate/audioQuality/framerate/
/// dimension/speed/anime), and dst/src fields are added.
/// </summary>
public sealed class TempPreset
{
    public FFmpegPreset Preset { get; }
    public DstArgs Dst { get; }

    public TempPreset(FFmpegPreset preset, DstArgs dst)
    {
        Preset = preset;
        Dst = dst;
    }

    // ---- preset fields ----
    public string Name => Preset.Name;
    public string? Type => Preset.Type;
    public string? Format => Preset.Format;
    public string? Prefix => Preset.Prefix;
    public string? Suffix => Preset.Suffix;
    public string? VideoCodecFamily => Preset.VideoCodecFamily;
    public string AudioCodec => Preset.AudioCodec;
    public string? AudioArgs => Preset.AudioArgs;
    public string? InputArgs => Preset.InputArgs;
    public string? StreamArgs => Preset.StreamArgs;
    public string? OutputArgs => Preset.OutputArgs;
    public string? Filters => Preset.Filters;
    public string? PreFilters => Preset.PreFilters;
    public string? PostFilters => Preset.PostFilters;
    public string? Output => Preset.Output;
    public bool? SmartBitrate => Preset.SmartBitrate;
    public PresetUserArgs UserArgs => Preset.UserArgs;

    // ---- overlaid (dstArgs wins) ----
    public long DstVideoBitrate => Dst.DstVideoBitrate;
    public long DstMaxBitrate => Dst.DstMaxBitrate;
    public long DstAudioBitrate => Dst.DstAudioBitrate;
    public long SrcAudioBitrate => Dst.SrcAudioBitrate;
    public long SrcVideoBitrate => Dst.SrcVideoBitrate;
    public bool Scaled => Dst.Scaled;

    public double VideoQuality => Dst.VideoQuality;      // overlay wins; caller falls back to preset
    public double PresetVideoQuality => Preset.VideoQuality;
    public double AudioQuality => Dst.AudioQuality;
    public double Framerate => Dst.Framerate;
    public long Dimension => Dst.Dimension;
    public double Speed => Dst.Speed;
    public bool Anime => Dst.Anime || Preset.UserArgs.Anime;

    /// <summary>Template dict = preset fields + dstArgs (JS spread order).</summary>
    public Dictionary<string, object?> ToTemplateDict()
    {
        var dict = new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["preset"] = Preset.Name,
            ["name"] = Preset.Name,
            ["format"] = Preset.Format,
            ["type"] = Preset.Type,
            ["prefix"] = Preset.Prefix,
            ["suffix"] = Preset.Suffix,
            ["videoCodecFamily"] = Preset.VideoCodecFamily,
            ["audioCodec"] = Preset.AudioCodec,
            ["inputArgs"] = Preset.InputArgs,
            ["streamArgs"] = Preset.StreamArgs,
            ["outputArgs"] = Preset.OutputArgs,
            ["filters"] = Preset.Filters,
            ["pre_filters"] = Preset.PreFilters,
            ["post_filters"] = Preset.PostFilters,
            ["output"] = Preset.Output,
            ["videoBitrate"] = Preset.VideoBitrate,
            ["maxBitrate"] = Preset.MaxBitrate,
            ["videoQuality"] = Preset.VideoQuality,
            ["audioBitrate"] = Preset.AudioBitrate,
            ["audioQuality"] = Preset.AudioQuality,
            ["dimension"] = Preset.Dimension,
            ["speed"] = Preset.Speed,
            ["framerate"] = Preset.Framerate,
            ["smartBitrate"] = Preset.SmartBitrate,
            ["audioArgs"] = Preset.AudioArgs,
        };
        foreach (var kv in Dst.ToTemplateDict()) dict[kv.Key] = kv.Value;
        return dict;
    }
}

/// <summary>Per-run CLI options relevant to the transcode core (subset of argv).</summary>
public sealed class ArgvOptions
{
    public string DecodeMode { get; set; } = "auto"; // auto | gpu | cpu
    public string? Hwaccel { get; set; }
    public bool Strict { get; set; }
    public bool Override { get; set; }
    public bool Debug { get; set; }
    public bool Anime { get; set; }
    public string Output { get; set; } = "";
    public string OutputMode { get; set; } = "dir";  // tree | dir | file
    public string? ErrorFile { get; set; }
}
