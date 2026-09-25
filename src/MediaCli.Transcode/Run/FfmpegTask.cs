using MediaCli.Transcode.Hardware;
using MediaCli.Transcode.MediaProbe;
using MediaCli.Transcode.Model;
using MediaCli.Transcode.Planning;
using MediaCli.Transcode.Support;

namespace MediaCli.Transcode.Run;

/// <summary>buildCliTask 依赖注入。</summary>
public sealed class TaskDeps
{
    public string? Output { get; set; }
    public string OutputMode { get; set; } = "dir";
    public string? FFprobePath { get; set; }
    public Func<string, MediaInfo?>? GetMediaInfo { get; set; }
    public Func<TranscodeEntry, DstArgs>? CalculateDstArgs { get; set; }
    public Func<TranscodeEntry, (string Base, string Prefix, string Suffix)>? CreateDstBaseName { get; set; }
    public Func<IReadOnlyList<string>, string?>? SelectPreferredSubtitle { get; set; }
}

/// <summary>
/// Port of ffmpeg_task.js: 单文件任务构建（prepare 阶段）。
/// 媒体信息探测 → 跳过判定 → 目标参数计算 → 目标路径生成 → 字幕选择 → 严格编码器预检。
/// </summary>
public static class FfmpegTask
{
    private static HardwareCaps? CachedCaps;

    /// <summary>Run 层复用硬件探测结果，避免重复探测。</summary>
    public static void SetCachedCaps(HardwareCaps caps) => CachedCaps = caps;
    internal static HardwareCaps? GetCachedCaps() => CachedCaps;

    public static TranscodeEntry BuildCliTask(TranscodeEntry entry, TaskDeps deps)
    {
        Func<string, TranscodeEntry> skipped = reason =>
        {
            entry.Skipped = true;
            entry.SkipReason = reason;
            return entry;
        };
        var mediaInfo = deps.GetMediaInfo ?? (p => FfprobeMediaInfo.GetMediaInfo(p, deps.FFprobePath));
        Func<TranscodeEntry, DstArgs> calculate = deps.CalculateDstArgs ?? FfmpegPlan.CalculateDstArgs;
        Func<TranscodeEntry, (string Base, string Prefix, string Suffix)> createBaseName = deps.CreateDstBaseName ?? FfmpegPlan.CreateDstBaseName;
        var chooseSubtitle = deps.SelectPreferredSubtitle ?? FfmpegPlan.SelectPreferredSubtitle;

        var preset = entry.Preset;
        var argv = entry.Argv;
        var isAudio = Helper.IsAudioFile(entry.Path);
        var srcDir = Path.GetDirectoryName(Path.GetFullPath(entry.Path)) ?? ".";
        var srcBase = Path.GetFileNameWithoutExtension(entry.Name);
        var srcExt = Path.GetExtension(entry.Name);
        var dstExt = !string.IsNullOrEmpty(preset.Format) ? preset.Format! : srcExt;
        string fileDstDir;

        if (!string.IsNullOrEmpty(deps.Output))
        {
            var output = Path.GetFullPath(deps.Output);
            switch (deps.OutputMode)
            {
                case "tree":
                    // pathRewrite 语义：srcDir 中 root 前缀替换为 preset.Output
                    var root = string.IsNullOrEmpty(entry.Root) ? srcDir : entry.Root;
                    var relative = srcDir.StartsWith(root, StringComparison.OrdinalIgnoreCase)
                        ? srcDir[root.Length..].TrimStart(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
                        : Path.GetFileName(srcDir);
                    fileDstDir = string.IsNullOrEmpty(relative) ? output : Path.Combine(output, relative);
                    break;
                case "file":
                    fileDstDir = output;
                    break;
                case "dir":
                    fileDstDir = Path.Combine(output, Path.GetFileName(srcDir));
                    break;
                default:
                    entry.Skipped = true;
                    entry.SkipReason = SkipReason.PrepareError;
                    return entry;
            }
        }
        else
        {
            fileDstDir = Path.GetFullPath(srcDir);
        }

        try
        {
            entry.Info = mediaInfo(entry.Path);
            var info = entry.Info;
            if (info is null || info.Duration <= 0 || info.Bitrate <= 0)
            {
                return skipped(SkipReason.BadFormat); // Skip[BadFormat]
            }

            var audioCodec = info.Audio?.Format;
            var videoCodec = info.Video?.Format;

            if (isAudio)
            {
                // 音频 tags：ffprobe format.tags → entry.Tags（readMusicMeta 的 C# 简化等价）
                entry.Tags = info.Tags.Count > 0 ? info.Tags : info.Audio?.Tags;
                entry.FormatMeta = new MusicMeta
                {
                    Bitrate = info.Audio?.Bitrate > 0 ? info.Audio.Bitrate : info.Bitrate,
                    Duration = info.Duration,
                    Codec = audioCodec,
                    Lossless = Helper.IsAudioLossless(entry.Path),
                };
                if (!(entry.FormatMeta.Bitrate > 0 || info.Audio?.Bitrate > 0 || info.Bitrate > 0))
                {
                    return skipped(SkipReason.InvalidAudio); // Skip[Invalid]
                }
            }

            entry.DstArgs = calculate(entry);

            if (preset.Type == "audio" && string.IsNullOrEmpty(audioCodec))
            {
                return skipped(SkipReason.MissingAudio); // Skip[NoAudio]
            }
            if (preset.Type == "video" && string.IsNullOrEmpty(videoCodec))
            {
                return skipped(SkipReason.MissingVideo); // Skip[NoVideo]
            }

            var (fileDstBase, prefix, suffix) = createBaseName(entry);
            var fileDstName = $"{fileDstBase}{dstExt}";
            var fileDst = Path.Combine(fileDstDir, fileDstName);
            var tempSuffix = $"_tmp@{Helper.TextHash(entry.Path)}@tmp_";
            var fileDstTemp = Path.Combine(fileDstDir, $"{fileDstBase}{tempSuffix}{dstExt}");
            var fileDstSameDir = Path.Combine(srcDir, fileDstName);

            if (File.Exists(fileDst))
            {
                if (!argv.Override)
                {
                    entry.DstExists = true;
                    entry.DstExistsPath = fileDst;
                    entry.DstExistsSize = new FileInfo(fileDst).Length;
                    return skipped(SkipReason.DestinationExists);
                }
            }

            if (string.IsNullOrEmpty(deps.Output) && (prefix.Length > 0 || suffix.Length > 0) && File.Exists(fileDstSameDir))
            {
                if (!argv.Override)
                {
                    entry.DstExists = true;
                    return skipped(SkipReason.DestinationExists);
                }
            }

            var ivideo = info.Video;
            var iaudio = info.Audio;
            var duration = info.Duration > 0 ? info.Duration
                : ivideo?.Duration > 0 ? ivideo.Duration : iaudio?.Duration ?? 0;
            if (duration < 1)
            {
                return skipped(SkipReason.ShortDuration); // Skip[Short]
            }

            // strict 模式音频编码器预检
            var targetAudioCodec = !string.IsNullOrEmpty(preset.UserArgs.AudioCodec)
                ? preset.UserArgs.AudioCodec : preset.AudioCodec;
            if (argv.Strict && !string.IsNullOrEmpty(targetAudioCodec) && targetAudioCodec != "copy")
            {
                try
                {
                    var caps = CachedCaps ?? HwDetect.DetectHardwareCapabilities();
                    if (caps.Encoders.Count > 0 && !caps.Encoders.Contains(targetAudioCodec))
                    {
                        return skipped(SkipReason.StrictCodec); // Skip[StrictCodec]
                    }
                }
                catch
                {
                    // codec precheck skipped（探测失败不阻塞）
                }
            }

            // 外挂字幕：srcDir 与 srcDir/subs 两处
            var subtitles = new List<string>();
            foreach (var ext in new[] { ".ass", ".ssa", ".srt" })
            {
                var sub1 = Path.Combine(srcDir, $"{srcBase}{ext}");
                var sub2 = Path.Combine(srcDir, "subs", $"{srcBase}{ext}");
                if (File.Exists(sub1)) subtitles.Add(sub1);
                if (File.Exists(sub2)) subtitles.Add(sub2);
            }

            entry.FileDstDir = fileDstDir;
            entry.FileDstBase = fileDstBase;
            entry.FileDst = fileDst;
            entry.FileDstTemp = fileDstTemp;
            entry.Subtitles = subtitles;
            entry.SelectedSubtitle = chooseSubtitle(subtitles);
            return entry;
        }
        catch
        {
            return skipped(SkipReason.PrepareError);
        }
    }
}
