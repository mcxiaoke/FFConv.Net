namespace MediaCli.Transcode.Presets;

/// <summary>Preset field types (port of preset_schema.js PRESET_FIELD_DEFS.type).</summary>
public enum PresetFieldType
{
    String,
    Number,
    Boolean,
}

/// <summary>Field definition (port of preset_schema.js PRESET_FIELD_DEFS entry).</summary>
public sealed record PresetFieldDef(PresetFieldType Type, bool Construct, string Comment);

/// <summary>
/// Port of preset_schema.js: the single source of truth for preset field names,
/// types and whether FFmpegPreset consumes them.
/// </summary>
public static class PresetSchema
{
    public static readonly IReadOnlyDictionary<string, PresetFieldDef> FieldDefs =
        new Dictionary<string, PresetFieldDef>(StringComparer.Ordinal)
        {
            // ---- inheritance / meta (loader + merge only) ----
            ["extends"] = new(PresetFieldType.String, false, "inherited preset name"),
            ["name"] = new(PresetFieldType.String, false, "preset name (YAML key)"),
            ["description"] = new(PresetFieldType.String, false, "preset description"),
            ["intro"] = new(PresetFieldType.String, false, "preset intro"),
            ["_override"] = new(PresetFieldType.Boolean, false, "explicit override flag"),
            // ---- FFmpegPreset constructor fields ----
            ["format"] = new(PresetFieldType.String, true, "output container format"),
            ["type"] = new(PresetFieldType.String, true, "media type: video / audio"),
            ["prefix"] = new(PresetFieldType.String, true, "output file prefix"),
            ["suffix"] = new(PresetFieldType.String, true, "output file suffix template"),
            ["audioCodec"] = new(PresetFieldType.String, true, "audio encoder"),
            ["inputArgs"] = new(PresetFieldType.String, true, "input args string"),
            ["streamArgs"] = new(PresetFieldType.String, true, "stream mapping args string"),
            ["outputArgs"] = new(PresetFieldType.String, true, "output args string"),
            ["filters"] = new(PresetFieldType.String, true, "video filter string with {scaleFilter} placeholder"),
            ["pre_filters"] = new(PresetFieldType.String, true, "pre-scale filter segment"),
            ["post_filters"] = new(PresetFieldType.String, true, "post-scale filter segment"),
            ["output"] = new(PresetFieldType.String, true, "output directory"),
            ["videoBitrate"] = new(PresetFieldType.String, true, "video bitrate with unit"),
            ["maxBitrate"] = new(PresetFieldType.String, true, "video peak bitrate with unit"),
            ["videoQuality"] = new(PresetFieldType.Number, true, "video quality (CRF/CQ)"),
            ["audioBitrate"] = new(PresetFieldType.String, true, "audio bitrate with unit"),
            ["audioQuality"] = new(PresetFieldType.Number, true, "audio quality (VBR)"),
            ["dimension"] = new(PresetFieldType.Number, true, "target long edge pixels"),
            ["speed"] = new(PresetFieldType.Number, true, "playback speed multiplier"),
            ["framerate"] = new(PresetFieldType.Number, true, "target frame rate"),
            ["smartBitrate"] = new(PresetFieldType.Boolean, true, "auto bitrate by size"),
            ["videoCodecFamily"] = new(PresetFieldType.String, true, "output codec family"),
        };

    /// <summary>All legal fields (loader whitelist).</summary>
    public static readonly HashSet<string> Fields = new(FieldDefs.Keys, StringComparer.Ordinal);

    /// <summary>Fields consumed by the FFmpegPreset constructor.</summary>
    public static readonly HashSet<string> ConstructorFields =
        new(FieldDefs.Where(kv => kv.Value.Construct).Select(kv => kv.Key), StringComparer.Ordinal);

    public static bool IsPresetField(string key) => Fields.Contains(key);

    /// <summary>True when the value type does not match the schema declaration.</summary>
    public static bool HasTypeMismatch(string key, object? value)
    {
        if (!FieldDefs.TryGetValue(key, out var meta)) return false;
        return meta.Type switch
        {
            PresetFieldType.Number => value is not (int or long or double or float) || (value is double d && double.IsNaN(d)),
            PresetFieldType.Boolean => value is not bool,
            PresetFieldType.String => value is not string,
            _ => false,
        };
    }
}
