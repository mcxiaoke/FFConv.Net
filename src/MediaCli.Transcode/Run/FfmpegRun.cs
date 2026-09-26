using System.Diagnostics;
using System.Text;
using System.Text.RegularExpressions;
using MediaCli.Transcode.Bin;
using MediaCli.Transcode.Hardware;
using MediaCli.Transcode.Model;
using MediaCli.Transcode.Planning;
using MediaCli.Transcode.Support;

namespace MediaCli.Transcode.Run;

/// <summary>runFFmpeg 可选项（C# 端用 CancellationToken 代替 AbortSignal）。</summary>
public sealed class RunOptions
{
    public Action<RunProgress>? OnProgress { get; init; }
    public Action<string>? OnLog { get; init; }
    public CancellationToken Signal { get; init; }
    public Action<Process>? OnSpawn { get; init; }
    public Action<(int Pid, int? Code)>? OnExit { get; init; }
}

/// <summary>进度回调数据。</summary>
public sealed record RunProgress(double Percent, string Speed, double CurrentTime, double SrcDuration);

/// <summary>
/// Port of ffmpeg_run.js: 单文件转码执行 —— 硬件分层决策、参数构建、进度解析、
/// 失败恢复、临时文件提交与清理。
/// </summary>
public static partial class FfmpegRun
{
    private const long FileSize1K = 1000;
    private const long FileSize1M = 1000 * 1000;

    private static string? ffmpegPath;

    public static void SetFFmpegPath(string p) => ffmpegPath = p;

    // ------------------------------------------------------------------
    // hardware plan (resolveHwPlan)
    // ------------------------------------------------------------------

    /// <summary>硬件加速分层决策（双层：设备能力 → 文件探测）。抛错由调用方走失败流程。</summary>
    private static HwPlan ResolveHwPlan(TranscodeEntry entry, CancellationToken signal)
    {
        signal.ThrowIfCancellationRequested();
        var ivideo = entry.Info?.Video;
        var iaudio = entry.Info?.Audio;

        // 音频文件或纯音频预设不做视频分层
        if (Helper.IsAudioFile(entry.Path) || entry.Preset.Type == "audio")
        {
            var cpuTier = Array.Find(HwAccel.Tiers, t => t.Name == "cpu")!;
            return new HwPlan
            {
                Tier = cpuTier,
                Size = null,
                Degraded = false,
                Tried = ["cpu"],
                Reason = Helper.IsAudioFile(entry.Path) ? "audio file" : "audio preset",
            };
        }

        var caps = HwDetect.DetectHardwareCapabilities(ffmpegPath);
        FfmpegTask.SetCachedCaps(caps);

        var decodeMode = string.IsNullOrEmpty(entry.Argv.DecodeMode) ? HwAccel.DecodeModeAuto : entry.Argv.DecodeMode;
        var srcW = ivideo?.Width ?? 0;
        var srcH = ivideo?.Height ?? 0;
        var pixFmt = ivideo?.PixelFormat ?? "";
        var codec = ivideo?.Format ?? "";
        var forcedEncoder = entry.Preset.UserArgs.VideoCodec;

        // 流复制不重新编码，无需硬件分层探测
        if (forcedEncoder == "copy")
        {
            var cpuTier = Array.Find(HwAccel.Tiers, t => t.Name == "cpu")!;
            return new HwPlan
            {
                Tier = cpuTier, Size = null, Degraded = false, Tried = ["cpu"],
                Reason = "video copy (no re-encode)", Caps = caps, DecodeMode = decodeMode,
            };
        }

        var codecFamily = HwAccel.CodecFamilyOfPreset(entry.Preset);
        var dimension = entry.DstArgs?.Dimension > 0 ? entry.DstArgs.Dimension : entry.Preset.Dimension;
        var quality = entry.DstArgs?.DstVideoQuality > 0 ? entry.DstArgs.DstVideoQuality
            : entry.Preset.VideoQuality > 0 ? entry.Preset.VideoQuality : 24;

        var plan = HwAccel.SelectTier(new SelectTierOptions
        {
            Caps = caps,
            FFmpegPath = ffmpegPath,
            InputPath = entry.Path,
            SrcW = srcW,
            SrcH = srcH,
            PixFmt = pixFmt,
            BitDepth = ivideo?.BitDepth,
            Codec = codec,
            CodecFamily = codecFamily,
            Dimension = dimension > 0 ? dimension : Math.Max(srcW, srcH),
            Speed = entry.DstArgs?.Speed is > 0 ? entry.DstArgs.Speed : null,
            Framerate = entry.DstArgs?.Framerate > 0 ? entry.DstArgs.Framerate : null,
            HasAudio = iaudio is not null,
            Quality = quality,
            Bitrate = entry.DstArgs?.DstVideoBitrate > 0 ? entry.DstArgs.DstVideoBitrate : null,
            MaxBitrate = entry.DstArgs?.DstMaxBitrate > 0 ? entry.DstArgs.DstMaxBitrate : null,
            ForcedEncoder = forcedEncoder,
            DecodeMode = decodeMode,
            Hwaccel = entry.Argv.Hwaccel,
            Strict = entry.Argv.Strict,
            Anime = entry.DstArgs?.Anime == true || entry.Preset.UserArgs.Anime || entry.Argv.Anime,
        });
        return new HwPlan
        {
            Tier = plan.Tier, Size = plan.Size, Degraded = plan.Degraded, Tried = plan.Tried,
            Reason = plan.Reason, Caps = caps, DecodeMode = decodeMode, ForcedEncoder = forcedEncoder,
        };
    }

    // ------------------------------------------------------------------
    // main entry (runFFmpegCmd)
    // ------------------------------------------------------------------

    public static TranscodeEntry RunFFmpeg(TranscodeEntry entry, RunOptions? options = null)
    {
        var signal = options?.Signal ?? CancellationToken.None;
        try
        {
            // ---- 硬件分层决策 + 参数构建 ----
            HwPlan hwPlan;
            try
            {
                hwPlan = ResolveHwPlan(entry, signal);
            }
            catch (OperationCanceledException)
            {
                entry.Cancelled = true;
                entry.CancelReason = "cancelled during planning";
                return entry;
            }
            catch (Exception error)
            {
                var errMsg = ExtractFFmpegError(error.Message, 200);
                // 严格模式：跳过该文件（不标记 ffmpegFailed）
                if (entry.Argv.Strict)
                {
                    entry.Skipped = true;
                    entry.SkipReason = SkipReason.StrictMode;
                    return entry;
                }
                entry.FFmpegFailed = true;
                entry.FFmpegError = $"plan: {errMsg}";
                WriteErrorFile(entry, error.Message);
                return entry;
            }
            entry.HwPlan = hwPlan;
            entry.UseCUDA = hwPlan.Tier.Name == "cuda";
            entry.FFmpegArgs = FfmpegBuild.CreateFFmpegArgs(entry, hwPlan).Args;
            if (signal.IsCancellationRequested)
            {
                entry.Cancelled = true;
                entry.CancelReason = "cancelled during planning";
                return entry;
            }

            // ---- dry-run 契约：与真实执行同路径，只在真正转码前停下 ----
            if (entry.TestMode)
            {
                entry.FFmpegFailed = true;
                entry.FFmpegError = "test-mode skip";
                return entry;
            }

            // 默认不触碰已有目标；override 只允许在临时文件成功提交时替换
            var shouldOverride = entry.Argv.Override;
            if (!shouldOverride && File.Exists(entry.FileDst))
            {
                entry.DstExists = true;
                entry.DstExistsPath = entry.FileDst;
                entry.DstExistsSize = new FileInfo(entry.FileDst).Length;
                return entry;
            }

            Directory.CreateDirectory(entry.FileDstDir!);
            if (File.Exists(entry.FileDstTemp)) File.Delete(entry.FileDstTemp);
            var ffmpegStartMs = Environment.TickCount64;

            IReadOnlyList<string> inputArgs = entry.FFmpegArgs![0];
            IReadOnlyList<string> middleArgs = entry.FFmpegArgs![1];
            IReadOnlyList<string> outputArgs = entry.FFmpegArgs![2];
            var metaComment = GetCommentArgs(entry);
            var ffmpegArgs = new List<string>(inputArgs);
            ffmpegArgs.AddRange(middleArgs);
            ffmpegArgs.AddRange(metaComment);
            ffmpegArgs.AddRange(outputArgs);

            options?.OnLog?.Invoke($"[PREPARE] {FfmpegPlan.GetEntryShowInfo(entry)}");
            options?.OnLog?.Invoke($"[CMD] ffmpeg {FfmpegBuild.FlattenFFArgs(entry.FFmpegArgs)}");

            try
            {
                ExecuteFFmpeg(ffmpegArgs, entry, options ?? new RunOptions(), signal);

                if (signal.IsCancellationRequested)
                {
                    entry.Cancelled = true;
                    entry.CancelReason = "cancelled before output commit";
                    return entry;
                }

                // 目标可能在执行期间由其它进程创建；再次检查避免竞态误覆盖
                if (File.Exists(entry.FileDst) && !shouldOverride)
                {
                    entry.DstExists = true;
                    entry.DstExistsPath = entry.FileDst;
                    entry.DstExistsSize = new FileInfo(entry.FileDst).Length;
                    if (File.Exists(entry.FileDstTemp)) File.Delete(entry.FileDstTemp);
                    return entry;
                }

                if (File.Exists(entry.FileDstTemp))
                {
                    var dstSize = new FileInfo(entry.FileDstTemp).Length;
                    // 产物异常小判定：源 >1MB 且产物 <=20KB（小源文件天然很小，不误判）
                    var dstTooSmall = dstSize <= 20 * FileSize1K;
                    var srcBigEnough = entry.Size > FileSize1M;
                    if (!dstTooSmall || !srcBigEnough)
                    {
                        if (signal.IsCancellationRequested)
                        {
                            entry.Cancelled = true;
                            entry.CancelReason = "cancelled before output commit";
                            return entry;
                        }
                        var committed = CommitOutputFile(entry.FileDstTemp!, entry.FileDst!, shouldOverride);
                        if (!committed)
                        {
                            entry.DstExists = true;
                            entry.DstExistsPath = entry.FileDst;
                            entry.DstExistsSize = File.Exists(entry.FileDst) ? new FileInfo(entry.FileDst).Length : 0;
                            return entry;
                        }
                        options?.OnLog?.Invoke(
                            $"[DONE] {entry.FileDst} ({Helper.HumanSize(entry.Size)}=>{Helper.HumanSize(dstSize)})");
                        entry.Ok = true;
                        return entry;
                    }
                    // 转换失败：产物异常小，删除临时文件
                }

                // ffmpeg 退出 0 但产物异常小/缺失：不设 ffmpegFailed（不进重试链路）
                entry.FFmpegError =
                    $"FFmpeg exited successfully but the output is {(File.Exists(entry.FileDstTemp) ? "unexpectedly small" : "missing")} (source: {Helper.HumanSize(entry.Size)})";
                return entry;
            }
            catch (Exception error)
            {
                if (error is OperationCanceledException || signal.IsCancellationRequested)
                {
                    entry.Cancelled = true;
                    entry.CancelReason = "cancelled during ffmpeg execution";
                    return entry;
                }
                var errMsg = ExtractFFmpegError(error.Message, 160);
                entry.FFmpegFailed = true;
                entry.FFmpegError = errMsg;
                WriteErrorFile(entry, errMsg);
                return entry;
            }
            finally
            {
                try
                {
                    if (File.Exists(entry.FileDstTemp)) File.Delete(entry.FileDstTemp);
                }
                catch
                {
                    // 清理失败不阻塞结果
                }
            }
        }
        finally
        {
            // no global cleanup needed in the C# port (temp files handled per-entry)
        }
    }

    /// <summary>将临时产物提交到最终路径（override=false 绝不触碰已有目标）。</summary>
    private static bool CommitOutputFile(string tempPath, string destinationPath, bool overrideExisting)
    {
        if (!File.Exists(destinationPath))
        {
            File.Move(tempPath, destinationPath);
            return true;
        }
        if (!overrideExisting)
        {
            File.Delete(tempPath);
            return false;
        }
        var backupPath = $"{destinationPath}.old@{DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()}_{Random.Shared.Next():x}";
        File.Move(destinationPath, backupPath);
        try
        {
            File.Move(tempPath, destinationPath);
        }
        catch
        {
            if (!File.Exists(destinationPath) && File.Exists(backupPath))
            {
                try { File.Move(backupPath, destinationPath); } catch { /* best effort */ }
            }
            throw;
        }
        try { if (File.Exists(backupPath)) File.Delete(backupPath); } catch { /* best effort */ }
        return true;
    }

    /// <summary>生成FFmpeg元数据注释参数（comment 写入实际转码完整命令行）。</summary>
    private static string[] GetCommentArgs(TranscodeEntry entry)
    {
        const int MaxCommentLen = 1000;
        var command = FfmpegBuild.FlattenFFArgs(entry.FFmpegArgs);
        if (string.IsNullOrEmpty(command))
        {
            return ["-metadata", "comment=mediac"];
        }
        var clean = command.Replace("'", " ").Replace("\"", " ");
        var commentText = $"mediac {clean}";
        if (commentText.Length > MaxCommentLen) commentText = commentText[..MaxCommentLen];
        return ["-metadata", $"comment={commentText}"];
    }

    /// <summary>在输出目录写入错误日志文件（writeErrorFile，失败忽略）。</summary>
    private static void WriteErrorFile(TranscodeEntry entry, string error)
    {
        if (string.IsNullOrEmpty(entry.Argv.ErrorFile) || string.IsNullOrEmpty(entry.FileDstDir)) return;
        try
        {
            Directory.CreateDirectory(entry.FileDstDir);
            var nowStr = DateTime.Now.ToString("yyyyMMddHHmmss");
            var errorFile = Path.Combine(entry.FileDstDir,
                $"{Path.GetFileNameWithoutExtension(entry.Name)}_{entry.Preset.Name}_error_{nowStr}.txt");
            var sb = new StringBuilder();
            sb.AppendLine($"path =: {entry.Path}");
            sb.AppendLine($"preset =: {entry.Preset.Name}");
            sb.AppendLine($"error =: {error}");
            sb.AppendLine($"date =: {DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()}");
            File.WriteAllText(errorFile, sb.ToString());
        }
        catch
        {
            // 日志失败不阻塞主流程
        }
    }

    // ------------------------------------------------------------------
    // process execution
    // ------------------------------------------------------------------

    private static void ExecuteFFmpeg(
        IReadOnlyList<string> args, TranscodeEntry entry, RunOptions options, CancellationToken externalSignal)
    {
        var srcDuration = entry.DstArgs?.SrcDuration > 0 ? entry.DstArgs.SrcDuration : entry.Info?.Duration ?? 0;
        var bin = ffmpegPath ?? FfmpegBin.ResolveFFmpegBinary();

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(externalSignal);
        var psi = new ProcessStartInfo
        {
            FileName = bin ?? "ffmpeg",
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = true,
            CreateNoWindow = true,
            // stdout 是 -progress 的 key=value 流（纯 ASCII），Latin1 可保证字节无损。
            StandardOutputEncoding = Encoding.Latin1,
            // stderr 是人类可读的错误文本，本机 ffmpeg 以 UTF-8 输出。
            // 实测 .NET 8 的默认解码已是 UTF-8（中文路径可正常显示），此处显式固定是
            // 为了不依赖宿主控制台状态：GUI 由资源管理器启动时与终端启动的码页可能不同，
            // 显式指定可保证日志里的中文路径始终可读。
            StandardErrorEncoding = Encoding.UTF8,
        };
        foreach (var a in args) psi.ArgumentList.Add(a);
        using var proc = new Process { StartInfo = psi };
        var stderrBuilder = new StringBuilder();
        var stdoutBuilder = new StringBuilder();
        var timeMatch = RxOutTime();
        var speedMatch = RxSpeed();
        var currentTime = 0.0;
        var currentSpeed = "0x";

        proc.OutputDataReceived += (_, e) =>
        {
            if (e.Data is null) return;
            lock (stdoutBuilder) stdoutBuilder.AppendLine(e.Data);
            foreach (var line in e.Data.Split('\n'))
            {
                var trimmed = line.Trim();
                var sm = speedMatch.Match(trimmed);
                if (sm.Success)
                {
                    var s = sm.Groups[1].Value.Trim();
                    if (s.Length > 0 && s != "N/A") currentSpeed = s;
                }
                var tm = timeMatch.Match(trimmed);
                if (tm.Success)
                {
                    currentTime = ParseTimeToSeconds(tm.Groups[1].Value.Trim());
                    if (srcDuration > 0)
                    {
                        var progress = Math.Min(100, Math.Round(currentTime / srcDuration * 100));
                        options.OnProgress?.Invoke(new RunProgress(progress, currentSpeed, currentTime, srcDuration));
                    }
                }
            }
        };
        proc.ErrorDataReceived += (_, e) =>
        {
            if (e.Data is null) return;
            lock (stderrBuilder) stderrBuilder.AppendLine(e.Data);
            options.OnLog?.Invoke(e.Data);
        };

        proc.Start();
        options.OnSpawn?.Invoke(proc);
        proc.BeginOutputReadLine();
        proc.BeginErrorReadLine();

        try
        {
            // ffmpeg 从 stdin 读取交互命令；关闭写端避免其等待输入
            proc.StandardInput.Close();
            using var reg = cts.Token.Register(() =>
            {
                try { proc.Kill(entireProcessTree: true); } catch { /* ignore */ }
            });
            if (!proc.WaitForExit(Timeout.Infinite))
            {
                throw new InvalidOperationException("ffmpeg wait failed");
            }
        }
        finally
        {
            options.OnExit?.Invoke((proc.Id, proc.HasExited ? proc.ExitCode : null));
        }

        cts.Token.ThrowIfCancellationRequested();
        if (proc.ExitCode != 0)
        {
            string stderr;
            lock (stderrBuilder) stderr = stderrBuilder.ToString();
            throw new FFmpegProcessException(proc.ExitCode, stderr);
        }
    }

    public sealed class FFmpegProcessException(int exitCode, string stderr) : Exception(stderr)
    {
        public int ExitCode { get; } = exitCode;
        public string StdErr { get; } = stderr;
    }

    [GeneratedRegex(@"^out_time=(.*)$")]
    private static partial Regex RxOutTime();
    [GeneratedRegex(@"^speed=\s*(.*)$")]
    private static partial Regex RxSpeed();

    /// <summary>ffmpeg 时间格式 HH:MM:SS.ms → 秒（NaN 防护）。</summary>
    private static double ParseTimeToSeconds(string timeStr)
    {
        if (string.IsNullOrEmpty(timeStr)) return 0;
        var parts = timeStr.Split(':');
        if (parts.Length != 3) return 0;
        if (!double.TryParse(parts[2], System.Globalization.CultureInfo.InvariantCulture, out var seconds) ||
            !double.IsFinite(seconds)) return 0;
        if (!double.TryParse(parts[0], System.Globalization.CultureInfo.InvariantCulture, out var hours) ||
            !double.TryParse(parts[1], System.Globalization.CultureInfo.InvariantCulture, out var minutes)) return 0;
        return hours * 3600 + minutes * 60 + seconds;
    }

    /// <summary>
    /// 从 ffmpeg stderr 提取有意义的错误行：从前往后找第一条 [error] 行
    /// （跳过 Link/Pixel formats/src:/dst: 噪声），其次特征词行，兜底最后一行。
    /// </summary>
    public static string ExtractFFmpegError(string? raw, int maxLen = 200)
    {
        if (string.IsNullOrEmpty(raw)) return "[Unknown]";
        var lines = raw.Split(["\r\n", "\n"], StringSplitOptions.None)
            .Select(l => l.Trim())
            .Where(l => l.Length > 0)
            .ToList();
        if (lines.Count == 0) return "[Unknown]";

        static string Strip(string s) => RxLevelPrefix().Replace(s, "");

        // 1) 第一条 [error] 行（错误块头部 = 根因）
        foreach (var line in lines)
        {
            if (RxErrorTag().IsMatch(line))
            {
                var body = Strip(line);
                if (RxNoiseLine().IsMatch(body)) continue;
                return body.Length > maxLen ? body[..maxLen] : body;
            }
        }
        // 2) 特征词行（排除无信息量的尾部总结）
        foreach (var line in lines)
        {
            var body = Strip(line);
            if (RxConversionFailed().IsMatch(body)) continue;
            if (RxErrorWord().IsMatch(body)) return body.Length > maxLen ? body[..maxLen] : body;
        }
        // 3) 兜底最后一行
        var last = Strip(lines[^1]);
        return last.Length > maxLen ? last[..maxLen] : last;
    }

    [GeneratedRegex(@"^\[[a-z]+\]\s*", RegexOptions.IgnoreCase)]
    private static partial Regex RxLevelPrefix();
    [GeneratedRegex(@"\[error\]", RegexOptions.IgnoreCase)]
    private static partial Regex RxErrorTag();
    [GeneratedRegex(@"^(link\s|pixel formats|src:|dst:)", RegexOptions.IgnoreCase)]
    private static partial Regex RxNoiseLine();
    [GeneratedRegex(@"^conversion failed!?$", RegexOptions.IgnoreCase)]
    private static partial Regex RxConversionFailed();
    [GeneratedRegex(@"error|invalid|failed|cannot|could not|unable|unsupported|not supported|no such|denied|corrupt|missing|out of range|exceed|truncat", RegexOptions.IgnoreCase)]
    private static partial Regex RxErrorWord();
}
