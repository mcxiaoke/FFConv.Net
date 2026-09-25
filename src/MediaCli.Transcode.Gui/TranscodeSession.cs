using MediaCli.Transcode.Build;
using MediaCli.Transcode.Model;
using MediaCli.Transcode.Presets;
using MediaCli.Transcode.Run;
using MediaCli.Transcode.Scan;

namespace MediaCli.Transcode.Gui;

/// <summary>日志级别（供 UI 着色）。</summary>
public enum SessionLogLevel
{
    Info,
    Cmd,
    Warn,
    Error,
    Done,
}

/// <summary>单文件处理结果分类。</summary>
public enum SessionFileOutcome
{
    /// <summary>预览成功（拿到了与真实执行一致的命令行）。</summary>
    Preview,
    Success,
    Failed,
    Skipped,
    Cancelled,
}

/// <summary>单文件处理结果。</summary>
public sealed class SessionFileResult
{
    public required string Name { get; init; }
    public required string Path { get; init; }
    public required SessionFileOutcome Outcome { get; init; }
    public string? OutputPath { get; init; }
    public string? Detail { get; init; }
    public string? Stage { get; init; }
    public string? Command { get; init; }
    public string? Tier { get; init; }
}

/// <summary>进度快照。</summary>
public sealed record SessionProgress(
    int FileIndex, int FileTotal, string FileName, double Percent, string Speed);

/// <summary>整批执行汇总。</summary>
public sealed class SessionSummary
{
    public int Preview { get; set; }
    public int Success { get; set; }
    public int Failed { get; set; }
    public int Skipped { get; set; }
    public int Cancelled { get; set; }
    public int Total { get; set; }
    public bool WasCancelled { get; set; }
}

/// <summary>
/// GUI 侧的串行编排循环 —— core 有意省略了 <c>ffmpeg_engine.js</c>（Engine 编排层），
/// 因此「收集输入 → 逐文件 prepare → 执行」这一段必须由调用方补上。
/// 本类型是 CLI <c>Program.RunPlan</c> 的等价物，语义逐条同构。
///
/// 设计约束：
/// - <b>不引用任何 WinForms 类型</b>，因此可被 xUnit 直接覆盖；
/// - <b>严格串行</b>：core 是同步模型 + 进程内静态缓存（probeCache / CachedCapabilities /
///   CachedCaps），并发会互相污染，且 NVENC/QSV 并发争显存；
/// - 预览与真实执行<b>同路径</b>：预览走 <c>TestMode</c>，先完成真实的硬件分层决策与
///   命令构建，再在真正转码前停下，因此预览不会撒谎。
/// </summary>
public sealed class TranscodeSession
{
    /// <summary>
    /// core 在 <c>TestMode</c> 分支写入的哨兵值。
    ///
    /// <c>FfmpegRun.RunFFmpeg</c> 走完 ResolveHwPlan + CreateFFmpegArgs 后，会设
    /// <c>FFmpegFailed = true; FFmpegError = "test-mode skip"</c> 并返回。UI 必须识别它，
    /// 否则每个文件都会被误报成红色失败。
    /// </summary>
    public const string TestModeSentinel = "test-mode skip";

    private static readonly object InitLock = new();
    private static bool presetsLoaded;

    private readonly Action<SessionLogLevel, string> log;
    private readonly Action<SessionProgress>? progress;

    public TranscodeSession(
        Action<SessionLogLevel, string> log,
        Action<SessionProgress>? progress = null)
    {
        this.log = log ?? throw new ArgumentNullException(nameof(log));
        this.progress = progress;
    }

    /// <summary>加载预设分层（进程内只做一次）。</summary>
    public static void EnsurePresetsLoaded()
    {
        lock (InitLock)
        {
            if (presetsLoaded) return;
            FFmpegPresets.Init();
            presetsLoaded = true;
        }
    }

    /// <summary>可选预设名（不含 <c>_base_*</c>，由 loader 的 merge 规则天然排除）。</summary>
    public static IReadOnlyList<string> PresetNames()
    {
        EnsurePresetsLoaded();
        return FFmpegPresets.GetAllNames();
    }

    /// <summary>按当前选项收集并过滤输入文件（供 UI 显示"已收集 N 个文件"）。</summary>
    public static List<ScanEntry> CollectFiles(GuiOptions options)
    {
        EnsurePresetsLoaded();
        var preset = FFmpegPresets.CreateFromArgv(options.ToArgvShim());
        return FilterForPreset(preset, options);
    }

    private static List<ScanEntry> FilterForPreset(FFmpegPreset preset, GuiOptions options)
    {
        var entries = FfmpegScan.CollectInputFiles(options.Inputs);
        return FfmpegScan.FilterAndSliceEntries(
            entries,
            preset.Type ?? "video",
            FFmpegPresets.IsAudioExtract(preset));
    }

    /// <summary>
    /// 执行一批文件。<paramref name="doit"/> 为 false 时只做预览（不写盘）。
    /// 取消通过 <paramref name="token"/> 传播到 core 的进程 kill 链路。
    /// </summary>
    public SessionSummary Run(GuiOptions options, bool doit, CancellationToken token)
    {
        EnsurePresetsLoaded();
        var summary = new SessionSummary();

        var argv = options.ToArgvOptions();
        var deps = options.ToTaskDeps();
        FFmpegPreset preset;
        try
        {
            preset = FFmpegPresets.CreateFromArgv(options.ToArgvShim());
        }
        catch (Exception ex)
        {
            log(SessionLogLevel.Error, $"preset 解析失败：{ex.Message}");
            return summary;
        }

        var filtered = FilterForPreset(preset, options);
        summary.Total = filtered.Count;
        if (filtered.Count == 0)
        {
            log(SessionLogLevel.Warn, "没有找到可处理的媒体文件（检查输入路径与预设的媒体类型）。");
            return summary;
        }

        log(SessionLogLevel.Info,
            $"预设 {preset.Name}（type={preset.Type ?? "-"} format={preset.Format ?? "-"}）" +
            $"  ·  共 {filtered.Count} 个文件  ·  {(doit ? "执行模式" : "预览模式（不写盘）")}");

        for (var i = 0; i < filtered.Count; i++)
        {
            if (token.IsCancellationRequested)
            {
                summary.WasCancelled = true;
                log(SessionLogLevel.Warn, "已取消，剩余文件未处理。");
                break;
            }

            var src = filtered[i];
            log(SessionLogLevel.Info, $"——— [{i + 1}/{filtered.Count}] {src.Name}");

            var entry = new TranscodeEntry
            {
                Index = i,
                Total = filtered.Count,
                Root = src.Root,
                Path = src.Path,
                Name = src.Name,
                Size = src.Size,
                Preset = preset.Clone(),
                Argv = argv,
                TestMode = !doit,
                StartMs = Environment.TickCount64,
            };

            TranscodeEntry task;
            try
            {
                task = FfmpegTask.BuildCliTask(entry, deps);
            }
            catch (Exception ex)
            {
                summary.Failed++;
                log(SessionLogLevel.Error, $"prepare 阶段异常：{ex.Message}");
                continue;
            }

            if (task.Skipped)
            {
                summary.Skipped++;
                log(SessionLogLevel.Warn, $"跳过[{task.SkipReason}] {task.Path}");
                continue;
            }

            var runOptions = new RunOptions
            {
                Signal = token,
                OnLog = ForwardCoreLog,
                OnProgress = p => progress?.Invoke(
                    new SessionProgress(i + 1, filtered.Count, src.Name, p.Percent, p.Speed)),
            };

            TranscodeEntry done;
            try
            {
                done = FfmpegRun.RunFFmpeg(task, runOptions);
            }
            catch (OperationCanceledException)
            {
                summary.Cancelled++;
                summary.WasCancelled = true;
                log(SessionLogLevel.Warn, "已取消。");
                break;
            }
            catch (Exception ex)
            {
                summary.Failed++;
                log(SessionLogLevel.Error, $"执行异常：{ex.Message}");
                continue;
            }

            Classify(done, summary);
        }

        log(SessionLogLevel.Info, BuildSummaryLine(summary));
        return summary;
    }

    /// <summary>
    /// 把 core 返回的 entry 归类到 UI 结果。
    ///
    /// 判定顺序刻意如此：<b>先</b>认 TestMode 哨兵，<b>再</b>看真实失败 ——
    /// 因为预览成功时 core 也会把 <c>FFmpegFailed</c> 置为 true。
    /// </summary>
    private void Classify(TranscodeEntry done, SessionSummary summary)
    {
        var tier = done.HwPlan?.Tier.Name;

        if (done.Cancelled)
        {
            summary.Cancelled++;
            summary.WasCancelled = true;
            log(SessionLogLevel.Warn, $"已取消：{done.CancelReason}");
            return;
        }

        // 预览成功：拿到了与真实执行一致的命令行
        if (done.TestMode &&
            string.Equals(done.FFmpegError, TestModeSentinel, StringComparison.Ordinal))
        {
            summary.Preview++;
            var cmd = FfmpegBuild.FlattenFFArgs(done.FFmpegArgs);
            log(SessionLogLevel.Cmd, $"ffmpeg {cmd}");
            log(SessionLogLevel.Info,
                $"预览就绪  ·  目标 {done.FileDst}  ·  硬件层 {tier ?? "-"}" +
                (string.IsNullOrEmpty(done.HwPlan?.Reason) ? "" : $"  ·  {done.HwPlan!.Reason}"));
            return;
        }

        if (done.Ok)
        {
            summary.Success++;
            log(SessionLogLevel.Done, $"完成：{done.FileDst}");
            return;
        }

        if (done.Skipped || done.DstExists)
        {
            summary.Skipped++;
            log(SessionLogLevel.Warn,
                $"跳过[{done.SkipReason ?? "destination_exists"}] {done.DstExistsPath ?? done.FileDst}");
            return;
        }

        summary.Failed++;
        var stage = done.FFmpegError is not null && done.FFmpegError.StartsWith("plan:", StringComparison.Ordinal)
            ? "plan"
            : "execute";
        log(SessionLogLevel.Error, $"失败[{stage}]：{done.FFmpegError ?? "未知错误"}");
    }

    private void ForwardCoreLog(string line)
    {
        var level = line.StartsWith("[DONE]", StringComparison.Ordinal) ? SessionLogLevel.Done
            : line.StartsWith("[CMD]", StringComparison.Ordinal) ? SessionLogLevel.Cmd
            : line.StartsWith("[PREPARE]", StringComparison.Ordinal) ? SessionLogLevel.Info
            : SessionLogLevel.Info;
        log(level, line);
    }

    private static string BuildSummaryLine(SessionSummary s)
    {
        var parts = new List<string>();
        if (s.Preview > 0) parts.Add($"预览 {s.Preview}");
        if (s.Success > 0) parts.Add($"成功 {s.Success}");
        if (s.Failed > 0) parts.Add($"失败 {s.Failed}");
        if (s.Skipped > 0) parts.Add($"跳过 {s.Skipped}");
        if (s.Cancelled > 0) parts.Add($"取消 {s.Cancelled}");
        if (parts.Count == 0) parts.Add("无处理项");
        return $"汇总：{string.Join("  ·  ", parts)}（共 {s.Total}）";
    }
}
