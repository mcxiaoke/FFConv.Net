using MediaCli.Transcode.Model;
using MediaCli.Transcode.Support;

namespace MediaCli.Transcode.Presets;

/// <summary>
/// Port of ffmpeg_presets.js: preset registry (single source = presets/default.yaml
/// plus user layers), preset construction from argv, ffargs alias application.
/// </summary>
public static class FFmpegPresets
{
    private static readonly List<string> PresetNames = [];
    private static readonly Dictionary<string, FFmpegPreset> PresetMap = new(StringComparer.Ordinal);

    /// <summary>Port of normalizeBitrate: "" / null / 0 → 0 (unset); otherwise parseBitrate.</summary>
    private static long NormalizeBitrate(object? value)
    {
        if (value is null or "" or 0) return 0;
        return Helper.ParseBitrate(value);
    }

    /// <summary>Port of initPresetsAsync: load layers low → high and construct FFmpegPresets.</summary>
    public static void Init(string? customPath = null)
    {
        var layers = PresetLoader.LoadPresetLayers(customPath);
        var merged = new Dictionary<string, Dictionary<string, object?>>(StringComparer.Ordinal);
        foreach (var layer in layers)
        {
            var raw = PresetLoader.ProcessPresets(layer.Presets);
            merged = PresetLoader.MergePresets(merged, new PresetLoader.Layer
            {
                Path = layer.Path,
                Presets = raw,
            });
        }
        lock (PresetMap)
        {
            PresetNames.Clear();
            PresetMap.Clear();
            foreach (var (name, fields) in merged)
            {
                PresetMap[name] = FromFields(name, fields);
                PresetNames.Add(name);
            }
        }
    }

    private static T? Get<T>(Dictionary<string, object?> fields, string key)
        => fields.TryGetValue(key, out var v) ? (T?)v : default;

    /// <summary>Construct an FFmpegPreset from a resolved raw field map.</summary>
    public static FFmpegPreset FromFields(string name, Dictionary<string, object?> fields)
    {
        var bitrateField = fields.TryGetValue("videoBitrate", out var vb) ? vb : 0;
        _ = bitrateField;
        return new FFmpegPreset
        {
            Name = name,
            Format = Get<string>(fields, "format"),
            Type = Get<string>(fields, "type"),
            Prefix = Get<string>(fields, "prefix"),
            Suffix = Get<string>(fields, "suffix"),
            VideoCodecFamily = Get<string>(fields, "videoCodecFamily"),
            AudioCodec = Get<string>(fields, "audioCodec") as string ?? "aac",
            InputArgs = Get<string>(fields, "inputArgs"),
            StreamArgs = Get<string>(fields, "streamArgs"),
            OutputArgs = Get<string>(fields, "outputArgs"),
            Filters = Get<string>(fields, "filters"),
            PreFilters = Get<string>(fields, "pre_filters"),
            PostFilters = Get<string>(fields, "post_filters"),
            Output = Get<string>(fields, "output"),
            VideoBitrate = NormalizeBitrate(fields.TryGetValue("videoBitrate", out var v) ? v : 0),
            MaxBitrate = NormalizeBitrate(fields.TryGetValue("maxBitrate", out var m) ? m : 0),
            VideoQuality = fields.TryGetValue("videoQuality", out var vq) && vq is not null ? Convert.ToDouble(vq) : 0,
            AudioBitrate = NormalizeBitrate(fields.TryGetValue("audioBitrate", out var ab) ? ab : 0),
            AudioQuality = fields.TryGetValue("audioQuality", out var aq) && aq is not null ? Convert.ToDouble(aq) : 0,
            Dimension = fields.TryGetValue("dimension", out var dim) && dim is not null ? Convert.ToInt64(dim) : 0,
            Speed = fields.TryGetValue("speed", out var sp) && sp is not null ? Convert.ToDouble(sp) : 1,
            Framerate = fields.TryGetValue("framerate", out var fr) && fr is not null ? Convert.ToDouble(fr) : 0,
            SmartBitrate = Get<bool>(fields, "smartBitrate"),
        };
    }

    public static FFmpegPreset? GetPreset(string name)
    {
        lock (PresetMap) return PresetMap.TryGetValue(name, out var p) ? p : null;
    }

    public static IReadOnlyDictionary<string, FFmpegPreset> GetAllPresets()
    {
        lock (PresetMap) return new Dictionary<string, FFmpegPreset>(PresetMap, StringComparer.Ordinal);
    }

    public static IReadOnlyList<string> GetAllNames()
    {
        lock (PresetMap) return [.. PresetNames];
    }

    /// <summary>Port of presets.isAudioExtract.</summary>
    public static bool IsAudioExtract(FFmpegPreset preset) => preset.Name == "audio_extract";

    // ------------------------------------------------------------------
    // ffargs alias application (port of ARG_ALIASES + applyFfargs)
    // ------------------------------------------------------------------

    private static readonly Dictionary<string, string> ArgAliases = new(StringComparer.Ordinal)
    {
        ["vb"] = "videoBitrate",
        ["vbit"] = "videoBitrate",
        ["vbk"] = "videoBitrate",
        ["vbitrate"] = "videoBitrate",
        ["vq"] = "videoQuality",
        ["vquality"] = "videoQuality",
        ["vc"] = "videoCodec",
        ["vcodec"] = "videoCodec",
        ["ab"] = "audioBitrate",
        ["abit"] = "audioBitrate",
        ["abk"] = "audioBitrate",
        ["abitrate"] = "audioBitrate",
        ["aq"] = "audioQuality",
        ["aquality"] = "audioQuality",
        ["ac"] = "audioCodec",
        ["acodec"] = "audioCodec",
        ["px"] = "prefix",
        ["pf"] = "prefix",
        ["sx"] = "suffix",
        ["sf"] = "suffix",
        ["sp"] = "speed",
        ["dm"] = "dimension",
        ["fps"] = "framerate",
        ["md"] = "metadata",
        ["meta"] = "metadata",
        ["metadata"] = "metadata",
        ["an"] = "anime",
        ["anime"] = "anime",
    };

    /// <summary>
    /// 别名表的只读视图。
    /// 供上层（如 GUI 的 ffargs 预检）判断键是否合法，避免各处复制别名表造成漂移。
    /// 仅暴露读取，不改变 applyFfargs 的任何行为。
    /// </summary>
    public static IReadOnlyDictionary<string, string> ArgAliasesView => ArgAliases;

    public sealed class ArgvShim
    {
        public long VideoBitrate { get; set; }
        public double VideoQuality { get; set; }
        public long AudioBitrate { get; set; }
        public double AudioQuality { get; set; }
        public long Dimension { get; set; }
        public double Speed { get; set; }
        public double Framerate { get; set; }
        public bool VideoCopy { get; set; }
        public bool AudioCopy { get; set; }
        public string? VideoCodec { get; set; }
        public string? AudioCodec { get; set; }
        public string? Prefix { get; set; }
        public string? Suffix { get; set; }
        public string? Preset { get; set; }
        public string? Metadata { get; set; }
        public bool Anime { get; set; }
    }

    /// <summary>
    /// Port of applyFfargs: applies parsed ffargs key/values onto an argv-shaped
    /// shim. Command-line single options (already set, non-zero) keep priority.
    /// Returns the same shim instance (mutated).
    /// </summary>
    public static ArgvShim ApplyFfargs(ArgvShim argv, IReadOnlyDictionary<string, object?> ffargs)
    {
        foreach (var (rawKey, value) in ffargs)
        {
            var key = ArgAliases.TryGetValue(rawKey, out var normalized) ? normalized : rawKey;
            if (value is null or "") continue;

            // "not provided" check: numeric 0 and boolean false count as unset
            // (mirrors the JS hasArgvValue fix for yargs default: 0).
            var hasArgvValue = key switch
            {
                "videoBitrate" or "videoQuality" or "audioBitrate" or "audioQuality"
                    or "dimension" or "speed" or "framerate" => Convert.ToDouble(GetShimValue(argv, key)) != 0,
                "videoCopy" or "audioCopy" => Convert.ToBoolean(GetShimValue(argv, key)),
                "videoCodec" or "audioCodec" or "prefix" or "suffix" or "preset" or "metadata" =>
                    !string.IsNullOrEmpty(GetShimValue(argv, key) as string),
                _ => false,
            };

            switch (key)
            {
                case "videoBitrate" or "audioBitrate":
                    if (!hasArgvValue && value is not string && Convert.ToDouble(value) > 0)
                        SetShimValue(argv, key, (long)Math.Round(Convert.ToDouble(value)));
                    break;
                case "videoQuality" or "audioQuality" or "dimension" or "framerate" or "speed":
                    if (!hasArgvValue && value is not string && Convert.ToDouble(value) > 0)
                        SetShimValue(argv, key, Convert.ToDouble(value));
                    break;
                case "videoCopy" or "audioCopy":
                    if (!hasArgvValue) SetShimValue(argv, key, Convert.ToBoolean(value));
                    break;
                case "videoCodec" or "audioCodec":
                    if (!hasArgvValue && value is string s && s.Length > 0) SetShimValue(argv, key, s);
                    break;
                case "prefix" or "suffix":
                    if (!hasArgvValue && value is string ps) SetShimValue(argv, key, ps);
                    break;
                case "preset":
                    if (!hasArgvValue && value is string pv) argv.Preset = pv;
                    break;
                case "metadata":
                    if (value is string mv && mv.Length > 0)
                        argv.Metadata = string.IsNullOrEmpty(argv.Metadata) ? mv : $"{argv.Metadata};{mv}";
                    break;
                default:
                    break; // non-whitelisted keys are ignored in the C# port (JS warns)
            }
        }
        return argv;
    }

    private static object? GetShimValue(ArgvShim argv, string key) => key switch
    {
        "videoBitrate" => argv.VideoBitrate,
        "videoQuality" => argv.VideoQuality,
        "audioBitrate" => argv.AudioBitrate,
        "audioQuality" => argv.AudioQuality,
        "dimension" => argv.Dimension,
        "speed" => argv.Speed,
        "framerate" => argv.Framerate,
        "videoCopy" => argv.VideoCopy,
        "audioCopy" => argv.AudioCopy,
        "videoCodec" => argv.VideoCodec,
        "audioCodec" => argv.AudioCodec,
        "prefix" => argv.Prefix,
        "suffix" => argv.Suffix,
        "preset" => argv.Preset,
        "metadata" => argv.Metadata,
        _ => null,
    };

    private static void SetShimValue(ArgvShim argv, string key, object? value)
    {
        switch (key)
        {
            case "videoBitrate": argv.VideoBitrate = Convert.ToInt64(value); break;
            case "videoQuality": argv.VideoQuality = Convert.ToDouble(value); break;
            case "audioBitrate": argv.AudioBitrate = Convert.ToInt64(value); break;
            case "audioQuality": argv.AudioQuality = Convert.ToDouble(value); break;
            case "dimension": argv.Dimension = Convert.ToInt64(value); break;
            case "speed": argv.Speed = Convert.ToDouble(value); break;
            case "framerate": argv.Framerate = Convert.ToDouble(value); break;
            case "videoCopy": argv.VideoCopy = Convert.ToBoolean(value); break;
            case "audioCopy": argv.AudioCopy = Convert.ToBoolean(value); break;
            case "videoCodec": argv.VideoCodec = (string?)value; break;
            case "audioCodec": argv.AudioCodec = (string?)value; break;
            case "prefix": argv.Prefix = (string?)value; break;
            case "suffix": argv.Suffix = (string?)value; break;
        }
    }

    /// <summary>Parse an ffargs string ("vb=3M,vq=23,vc=h264_nvenc") into key/value pairs.</summary>
    public static IReadOnlyDictionary<string, object?> ParseFfargs(string? ffargs)
    {
        var result = new Dictionary<string, object?>(StringComparer.Ordinal);
        if (string.IsNullOrWhiteSpace(ffargs)) return result;
        foreach (var seg in ffargs.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var idx = seg.IndexOf('=');
            if (idx <= 0) continue;
            var key = seg[..idx].Trim();
            var value = seg[(idx + 1)..].Trim();
            if (key.Length == 0 || value.Length == 0) continue;
            if (long.TryParse(value, out var l)) result[key] = l;
            else if (double.TryParse(value, System.Globalization.CultureInfo.InvariantCulture, out var d)) result[key] = d;
            else result[key] = value;
        }
        return result;
    }

    /// <summary>Port of createFromArgv: clone base preset and apply argv/user overrides.</summary>
    public static FFmpegPreset CreateFromArgv(ArgvShim argv)
    {
        var presetName = argv.Preset ?? "hevc_2k";
        var isAnime = argv.Anime;
        if (presetName is "anime" or "hevc_anime")
        {
            presetName = "hevc_2k";
            isAnime = true;
        }
        else if (presetName == "av1_anime")
        {
            presetName = "av1_2k";
            isAnime = true;
        }
        else if (presetName == "h264_anime")
        {
            presetName = "h264_2k";
            isAnime = true;
        }
        else if (presetName == "av1") presetName = "av1_2k";
        else if (presetName == "hevc") presetName = "hevc_2k";
        else if (presetName == "h264") presetName = "h264_2k";

        var basePreset = GetPreset(presetName) ?? throw new KeyNotFoundException($"Preset not found: {presetName}");
        var preset = basePreset.Clone();
        if (isAnime) preset.UserArgs.Anime = true;

        if (argv.Prefix is not null) preset.Prefix = argv.Prefix;
        if (argv.Suffix is not null) preset.Suffix = argv.Suffix;

        if (!string.IsNullOrEmpty(argv.VideoCodec))
        {
            preset.UserArgs.VideoCodec = argv.VideoCodec;
            if (argv.VideoCodec == "copy")
            {
                preset.UserArgs.VideoCopy = true;
                preset.Filters = "";
                preset.PreFilters = "";
                preset.PostFilters = "";
            }
        }
        if (!string.IsNullOrEmpty(argv.AudioCodec))
        {
            preset.AudioCodec = argv.AudioCodec;
            preset.UserArgs.AudioCodec = argv.AudioCodec;
        }
        if (argv.Dimension > 0) preset.UserArgs.Dimension = argv.Dimension;
        if (argv.Speed > 0) preset.UserArgs.Speed = argv.Speed;
        if (argv.Framerate > 0) preset.UserArgs.Framerate = argv.Framerate;

        if (argv.VideoCopy)
        {
            preset.UserArgs.VideoCodec = "copy";
            preset.UserArgs.VideoCopy = true;
            preset.Filters = "";
        }
        else
        {
            if (argv.VideoBitrate != 0) preset.UserArgs.VideoBitrate = Helper.ParseBitrate(argv.VideoBitrate);
            if (argv.VideoQuality > 0) preset.UserArgs.VideoQuality = (long)argv.VideoQuality;
        }

        if (argv.AudioCopy)
        {
            preset.UserArgs.AudioCopy = true;
        }
        else
        {
            if (argv.AudioBitrate != 0) preset.UserArgs.AudioBitrate = Helper.ParseBitrate(argv.AudioBitrate);
            if (argv.AudioQuality > 0) preset.UserArgs.AudioQuality = (long)argv.AudioQuality;
        }

        if (!string.IsNullOrWhiteSpace(argv.Metadata))
        {
            var pairs = new List<KeyValuePair<string, string>>();
            foreach (var seg in argv.Metadata.Split(';'))
            {
                var s = seg.Trim();
                if (s.Length == 0 || !s.Contains('=')) continue;
                var idx = s.IndexOf('=');
                var key = s[..idx].Trim();
                var value = s[(idx + 1)..]; // keep inner spaces
                if (key.Length == 0) continue;
                pairs.Add(new KeyValuePair<string, string>(key, value));
            }
            if (pairs.Count > 0) preset.UserArgs.MetadataPairs = pairs;
        }
        return preset;
    }
}
