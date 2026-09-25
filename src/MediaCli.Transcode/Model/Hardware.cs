namespace MediaCli.Transcode.Model;

/// <summary>Output size (even numbers), port of hwaccel.js {w,h}.</summary>
public sealed record Size(int W, int H);

/// <summary>
/// One hardware tier definition. Port of hwaccel.js TIERS entries
/// (name/vendor/hwaccel/hwFormat/filter/filterArgs/requiresFilter) plus the
/// swdec-layer dynamic encoderRow injected by resolveTiers.
/// </summary>
public sealed class TierDef
{
    public required string Name { get; init; }
    public required string Vendor { get; init; }
    public string? Hwaccel { get; init; }
    public string? HwFormat { get; init; }
    public required string Filter { get; init; }
    public required string FilterArgs { get; init; }
    public bool RequiresFilter { get; init; }

    /// <summary>Vendor-specific encoder row for the swdec tier (h264/hevc/av1/vp9 → encoder).</summary>
    public IReadOnlyDictionary<string, string>? EncoderRow { get; init; }
}

/// <summary>Normalized GPU info entry (port of gpu.js detectGpus result).</summary>
public sealed class GpuInfo
{
    public required string Vendor { get; init; }
    public required string Model { get; init; }
    public int? Generation { get; init; }
    public string? DriverVersion { get; init; }
    public string? Bus { get; init; }
    public bool Primary { get; init; }
}

public sealed class GpuDecodeProbeEntry
{
    public required string Codec { get; init; }
    public string? Chroma { get; init; }
    public int? BitDepth { get; init; }
    public required string Support { get; init; } // yes | no | partial
}

public sealed class GpuEncodeProbeEntry
{
    public required string Codec { get; init; }
    public required string Format { get; init; }
    public required string Support { get; init; }
}

/// <summary>NVIDIA pre-probe list (port of gpu.js gpuProbeList result).</summary>
public sealed class GpuProbe
{
    public required string Vendor { get; init; }
    public required int Generation { get; init; }
    public string? Arch { get; init; }
    public required string Model { get; init; }
    public List<GpuDecodeProbeEntry> Decode { get; init; } = [];
    public List<GpuEncodeProbeEntry> Encode { get; init; } = [];
}

/// <summary>
/// Device-level hardware capabilities. Port of hwdetect.js
/// detectHardwareCapabilities() result (caps).
/// </summary>
public sealed class HardwareCaps
{
    public required string FFmpegPath { get; init; }
    public string Version { get; init; } = "";
    public string Configuration { get; init; } = "";
    public string BuildKind { get; init; } = "default";
    public HashSet<string> Encoders { get; init; } = new(StringComparer.Ordinal);
    public HashSet<string> Filters { get; init; } = new(StringComparer.Ordinal);
    public required string Vendor { get; init; }
    public required Dictionary<string, bool> Usable { get; init; }
    public required Dictionary<string, bool> StaticOk { get; init; }
    public Dictionary<string, string> ProbeDetail { get; init; } = new();
    public List<string> Hwaccels { get; init; } = [];
    public Dictionary<string, bool> FilterSupport { get; init; } = new();
    public int EncoderCount { get; init; }
    public List<GpuInfo> Gpus { get; init; } = [];
    public GpuProbe? GpuProbe { get; init; }
}

/// <summary>
/// Per-file hardware plan: the selected tier plus decision metadata.
/// Port of hwaccel.js selectTier / ffmpeg_run.js resolveHwPlan result.
/// </summary>
public sealed class HwPlan
{
    public required TierDef Tier { get; init; }
    public Size? Size { get; init; }
    public bool Degraded { get; init; }
    public List<string> Tried { get; init; } = [];
    public string Reason { get; init; } = "";
    public HardwareCaps? Caps { get; init; }
    public string? DecodeMode { get; init; }
    public string? ForcedEncoder { get; init; }
}
