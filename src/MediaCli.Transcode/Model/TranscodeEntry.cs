namespace MediaCli.Transcode.Model;

/// <summary>Scan result entry (port of ffmpeg_scan.js collectInputFiles items).</summary>
public sealed class ScanEntry
{
    public required string Root { get; init; }
    public required string Path { get; init; }
    public required string Name { get; init; }
    public long Size { get; init; }
}

/// <summary>Run status (port of ffmpeg_result.js RUN_STATUS).</summary>
public static class RunStatus
{
    public const string Success = "success";
    public const string Failed = "failed";
    public const string Skipped = "skipped";
    public const string Cancelled = "cancelled";
}

/// <summary>Skip reasons (port of ffmpeg_result.js SKIP_REASON).</summary>
public static class SkipReason
{
    public const string BadFormat = "bad_format";
    public const string InvalidAudio = "invalid_audio";
    public const string MissingAudio = "missing_audio";
    public const string MissingVideo = "missing_video";
    public const string DestinationExists = "destination_exists";
    /// <summary>
    /// 目标路径已被<b>本批次早前的文件</b>占用（不是磁盘上本来就有的旧文件）。
    /// 典型成因：同一基名、不同容器的文件在默认后缀 `_{preset}` 下映射到同一个目标名。
    /// 与 destination_exists 分开报告，用户才知道该改命名模板而不是去清理旧文件。
    /// </summary>
    public const string DestinationConflictInBatch = "destination_conflict_in_batch";
    public const string ShortDuration = "short_duration";
    public const string StrictCodec = "strict_codec";
    public const string StrictMode = "strict_mode";
    public const string PrepareError = "prepare_error";
}

/// <summary>Stable adapter-layer result (port of ffmpeg_result.js toRunResult).</summary>
public sealed class RunResult
{
    public required string Status { get; init; }
    public string Stage { get; init; } = "execute";
    public string? OutputPath { get; init; }
    public string? Reason { get; init; }
    public string? Error { get; init; }
}

/// <summary>
/// One transcode file entry (port of the JS entry object flowing through
/// ffmpeg_task → ffmpeg_plan → ffmpeg_build → ffmpeg_run).
/// </summary>
public sealed class TranscodeEntry
{
    public int Index { get; set; }
    public int Total { get; set; }
    public string Root { get; set; } = "";
    public required string Path { get; set; }
    public required string Name { get; set; }
    public long Size { get; set; }

    public MediaInfo? Info { get; set; }
    public FFmpegPreset Preset { get; set; } = new();
    public ArgvOptions Argv { get; set; } = new();
    public double Duration { get; set; }

    public DstArgs? DstArgs { get; set; }
    public MusicMeta? FormatMeta { get; set; }
    public Dictionary<string, string>? Tags { get; set; }

    public string? FileDstDir { get; set; }
    public string? FileDstBase { get; set; }
    public string? FileDst { get; set; }
    public string? FileDstTemp { get; set; }
    public List<string> Subtitles { get; set; } = [];
    public string? SelectedSubtitle { get; set; }

    // ---- run state ----
    public bool TestMode { get; set; }
    public bool RetryOnFailed { get; set; }
    public bool Ok { get; set; }
    public bool Skipped { get; set; }
    public string? SkipReason { get; set; }
    public bool Cancelled { get; set; }
    public string? CancelReason { get; set; }
    public bool FFmpegFailed { get; set; }
    public string? FFmpegError { get; set; }
    public bool DstExists { get; set; }
    public string? DstExistsPath { get; set; }
    public long DstExistsSize { get; set; }
    /// <summary>占用该目标路径的源文件（仅当冲突来自本批次早前文件时设置）。</summary>
    public string? ConflictedWithSource { get; set; }
    public HwPlan? HwPlan { get; set; }
    public bool UseCUDA { get; set; }
    /// <summary>[inputArgs, middleArgs, outputArgs] produced by createFFmpegArgs.</summary>
    public IReadOnlyList<string>[]? FFmpegArgs { get; set; }
    public long StartMs { get; set; }

    /// <summary>Port of ffmpeg_result.js toRunResult.</summary>
    public RunResult ToRunResult()
    {
        if (Cancelled)
            return new RunResult { Status = RunStatus.Cancelled, Reason = CancelReason ?? "cancelled" };
        if (Ok)
            return new RunResult { Status = RunStatus.Success, OutputPath = FileDst };
        if (Skipped || DstExists)
        {
            var reason = SkipReason;
            string[] knownReasons =
            [
                "bad_format", "invalid_audio", "missing_audio", "missing_video",
                "destination_exists", "short_duration", "strict_codec", "strict_mode", "prepare_error",
            ];
            if (string.IsNullOrEmpty(reason) || !knownReasons.Contains(reason))
            {
                reason = DstExists ? "destination_exists" : "prepare_error";
            }
            return new RunResult
            {
                Status = RunStatus.Skipped,
                Reason = reason,
                OutputPath = DstExistsPath ?? FileDst,
            };
        }
        var stage = FFmpegError is not null && FFmpegError.StartsWith("plan:") ? "plan" : "execute";
        return new RunResult { Status = RunStatus.Failed, Stage = stage, Error = FFmpegError ?? "Conversion failed" };
    }
}
