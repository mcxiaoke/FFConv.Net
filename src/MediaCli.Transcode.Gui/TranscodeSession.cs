using System.Diagnostics;
using MediaCli.Transcode.Planning;
using MediaCli.Transcode.Model;
using MediaCli.Transcode.Presets;
using MediaCli.Transcode.Run;
using MediaCli.Transcode.Scan;
using MediaCli.Transcode.Support;

namespace MediaCli.Transcode.Gui;

/// <summary>日志级别（供 UI 着色）。</summary>
public enum SessionLogLevel
{
    Info,
    Cmd,
    Warn,
    Error,
    Done,
    /// <summary>文件内部进度（按文件重复出现，UI 可弱化显示）。</summary>
    Progress,
}

/// <summary>单文件处理结果分类。</summary>
public enum SessionFileOutcome
{
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
    /// <summary>
    /// 当跳过原因是「目标已被本批次早前文件占用」时，记录占用该目标的源文件路径。
    /// </summary>
    public string? ConflictWith { get; init; }
    public string? Stage { get; init; }
    public string? Command { get; init; }
    public string? Tier { get; init; }
    public long InputBytes { get; init; }
    public long OutputBytes { get; init; }
    public long ElapsedMs { get; init; }
}

/// <summary>
/// 进度快照。
///
/// 进度条按<b>文件级</b>推进（需求：显示「正在处理 122/200」），
/// 同时带上文件内部占比，使单个大文件的进度条也能平滑移动；
/// 文件内部的百分比与 speed 只写进日志，不占用状态区。
/// </summary>
public sealed record SessionProgress(
    int FileIndex,
    int FileTotal,
    string FileName,
    int CompletedFiles,
    double OverallPercent,
    double WithinPercent,
    string Speed);

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

    /// <summary>整批耗时（毫秒）。</summary>
    public long ElapsedMs { get; set; }
    /// <summary>参与处理的输入文件总字节数。</summary>
    public long InputBytes { get; set; }
    /// <summary>实际产出的输出文件总字节数。</summary>
    public long OutputBytes { get; set; }
    /// <summary>实际产出的文件个数（成功 + 预览不产出，故仅统计成功）。</summary>
    public int OutputCount { get; set; }
    /// <summary>被日志过滤器隐藏的 ffmpeg 常规输出行数。</summary>
    public int SuppressedLines { get; set; }

    public List<SessionFileResult> Results { get; } = [];

    public int Processed => Success + Failed + Skipped;
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

    /// <summary>文件内部进度写日志的最小间隔（百分比）。避免刷屏。</summary>
    private const int ProgressLogStepPercent = 10;
    /// <summary>文件内部进度写日志的最小间隔（秒）。</summary>
    private const double ProgressLogStepSeconds = 5;

    private static readonly object InitLock = new();
    private static bool presetsLoaded;

    private readonly Action<SessionLogLevel, string> log;
    private readonly Action<SessionProgress>? progress;
    private readonly bool verboseLog;

    public TranscodeSession(
        Action<SessionLogLevel, string> log,
        Action<SessionProgress>? progress = null,
        bool verboseLog = false)
    {
        this.log = log ?? throw new ArgumentNullException(nameof(log));
        this.progress = progress;
        // 「详细日志」开关必须作用到这里：core 在 Debug 下会把 ffmpeg 切到
        // repeat+level+info（stderr 量很大），若此处仍按错误词表过滤，
        // 用户勾选后几乎看不到额外输出，而统计块还会显示"已隐藏 N 行（勾选详细日志可全部显示）"，
        // 提示与事实自相矛盾。
        this.verboseLog = verboseLog;
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
        options.RefreshScanFilters();
        var entries = FfmpegScan.CollectInputFiles(options.Inputs);
        return FfmpegScan.FilterAndSliceEntries(
            entries,
            preset.Type ?? "video",
            FFmpegPresets.IsAudioExtract(preset),
            include: options.Include,
            exclude: options.Exclude,
            regex: options.Regex,
            start: options.Start,
            count: options.Count);
    }

    /// <summary>
    /// 执行一批文件。<paramref name="doit"/> 为 false 时只做预览（不写盘）。
    /// 取消通过 <paramref name="token"/> 传播到 core 的进程 kill 链路。
    /// </summary>
    public SessionSummary Run(GuiOptions options, bool doit, CancellationToken token)
    {
        EnsurePresetsLoaded();
        var summary = new SessionSummary();
        var batchWatch = Stopwatch.StartNew();

        var argv = options.ToArgvOptions();
        var deps = options.ToTaskDeps();
        FFmpegPreset preset;
        try
        {
            preset = FFmpegPresets.CreateFromArgv(options.ToArgvShim());
        }
        catch (Exception ex)
        {
            log(SessionLogLevel.Error, $"预设解析失败：{ex.Message}");
            return summary;
        }

        var filtered = FilterForPreset(preset, options);
        summary.Total = filtered.Count;
        summary.InputBytes = filtered.Sum(e => e.Size);
        if (filtered.Count == 0)
        {
            log(SessionLogLevel.Warn, "没有找到可处理的媒体文件（检查输入路径、预设的媒体类型与筛选参数）。");
            return summary;
        }

        log(SessionLogLevel.Info,
            $"预设 {preset.Name}（type={preset.Type ?? "-"} format={preset.Format ?? "-"}）" +
            $"  ·  共 {filtered.Count} 个文件  ·  {(doit ? "执行模式" : "预览模式（不写盘）")}");

        // CLI 参数覆盖界面控件的说明必须可见，否则用户会以为参数没生效
        foreach (var note in options.AllWarnings()) log(SessionLogLevel.Warn, note);

        // 本批次已承诺的输出路径 → 产出它的源文件。
        //
        // 用途：同一基名、不同容器的文件在默认后缀 `_{preset}` 下会映射到同一个目标名，
        // 先跑成功的会占用该路径，后来者只看到"目标已存在"，无法分辨这是
        // 「本批自己刚产出的」还是「输出目录里本来就有的旧文件」。
        // 两者对用户意味着完全不同的动作（改命名模板 vs 清理旧文件），必须分开报告。
        //
        // 键用 OrdinalIgnoreCase：Windows 路径不区分大小写，仅大小写不同的目标名同样会互相覆盖。
        var batchOutputs = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

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
                summary.Results.Add(new SessionFileResult
                {
                    Name = src.Name, Path = src.Path, Outcome = SessionFileOutcome.Failed,
                    Stage = "prepare", Detail = ex.Message, InputBytes = src.Size,
                });
                continue;
            }

            if (task.Skipped)
            {
                summary.Skipped++;
                // 区分「本批自己刚产出的目标」与「输出目录里本来就有的旧文件」：
                // 前者要改命名模板，后者要清理旧文件或勾选覆盖。
                FfmpegTask.RefineDestinationConflict(task, batchOutputs);
                log(SessionLogLevel.Warn, DescribeSkip(task, src));
                summary.Results.Add(new SessionFileResult
                {
                    Name = src.Name, Path = src.Path, Outcome = SessionFileOutcome.Skipped,
                    Detail = task.SkipReason,
                    OutputPath = task.DstExistsPath ?? task.FileDst,
                    ConflictWith = task.ConflictedWithSource,
                    InputBytes = src.Size,
                });
                continue;
            }

            // 登记本文件即将占用的输出路径；后到的同目标文件即可被识别为批内冲突。
            if (task.FileDst is { Length: > 0 } plannedOutput)
            {
                batchOutputs.TryAdd(plannedOutput, src.Path);
            }

            var runOptions = new RunOptions
            {
                Signal = token,
                OnLog = ForwardCoreLog,
                OnProgress = p => OnFileProgress(p, i, filtered.Count, src.Name),
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
                summary.Results.Add(new SessionFileResult
                {
                    Name = src.Name, Path = src.Path, Outcome = SessionFileOutcome.Failed,
                    Stage = "execute", Detail = ex.Message, InputBytes = src.Size,
                });
                continue;
            }

            // 成功产出即登记该输出路径，供后到的同目标文件识别为「批内冲突」。
            if (done.Ok && done.FileDst is { Length: > 0 } produced)
            {
                batchOutputs.TryAdd(produced, src.Path);
            }
            Classify(done, src, summary, batchOutputs);
            var completedCount = i + 1;
            var fileOverall = (double)completedCount / filtered.Count * 100.0;
            progress?.Invoke(new SessionProgress(
                completedCount, filtered.Count, src.Name, completedCount, fileOverall, 100, ""));
        }

        batchWatch.Stop();
        summary.ElapsedMs = batchWatch.ElapsedMilliseconds;
        log(SessionLogLevel.Info, BuildSummaryLine(summary));
        return summary;
    }

    /// <summary>
    /// 文件内部进度：转成整批进度并回调 UI；同时按节流写入日志。
    ///
    /// 需求要求进度条以「文件」为单位（122/200），文件内部的百分比与 speed
    /// 只在日志里出现——因此这里把内部进度降级为日志行，并做节流避免刷屏。
    /// </summary>
    private void OnFileProgress(RunProgress p, int index, int total, string fileName)
    {
        var completed = index;
        var within = Math.Clamp(p.Percent, 0, 100);
        var overall = total == 0 ? 0 : (completed + within / 100.0) / total * 100.0;

        progress?.Invoke(new SessionProgress(
            index + 1, total, fileName, completed, overall, within, p.Speed));

        // 节流：仅在跨过百分比台阶或间隔足够久时写日志
        var now = DateTime.UtcNow;
        var key = index;
        if (lastProgressLogFile == key)
        {
            var crossedStep = within - lastProgressLogPercent >= ProgressLogStepPercent;
            var elapsed = (now - lastProgressLogAt).TotalSeconds >= ProgressLogStepSeconds;
            if (!crossedStep && !elapsed) return;
        }

        lastProgressLogFile = key;
        lastProgressLogPercent = within;
        lastProgressLogAt = now;

        var speed = string.IsNullOrEmpty(p.Speed) ? "" : $"  {p.Speed}";
        log(SessionLogLevel.Progress,
            $"[{index + 1}/{total}] {fileName}  文件内 {within:F0}%{speed}");
    }

    private int lastProgressLogFile = -1;
    private double lastProgressLogPercent = -1;
    private DateTime lastProgressLogAt = DateTime.MinValue;

    /// <summary>
    /// 把 core 返回的 entry 归类到 UI 结果。
    ///
    /// 判定顺序刻意如此：<b>先</b>认 TestMode 哨兵，<b>再</b>看真实失败 ——
    /// 因为预览成功时 core 也会把 <c>FFmpegFailed</c> 置为 true。
    /// </summary>
    private void Classify(TranscodeEntry done, ScanEntry src, SessionSummary summary,
        Dictionary<string, string> batchOutputs)
    {
        var tier = done.HwPlan?.Tier.Name;
        var elapsed = Environment.TickCount64 - done.StartMs;

        if (done.Cancelled)
        {
            summary.Cancelled++;
            summary.WasCancelled = true;
            log(SessionLogLevel.Warn, $"已取消：{done.CancelReason}");
            summary.Results.Add(new SessionFileResult
            {
                Name = src.Name, Path = src.Path, Outcome = SessionFileOutcome.Cancelled,
                Detail = done.CancelReason, InputBytes = src.Size, ElapsedMs = elapsed,
            });
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
            summary.Results.Add(new SessionFileResult
            {
                Name = src.Name, Path = src.Path, Outcome = SessionFileOutcome.Preview,
                OutputPath = done.FileDst, Command = cmd, Tier = tier,
                InputBytes = src.Size, ElapsedMs = elapsed,
            });
            return;
        }

        if (done.Ok)
        {
            summary.Success++;
            var outSize = FileSizeOf(done.FileDst);
            summary.OutputBytes += outSize;
            summary.OutputCount++;
            log(SessionLogLevel.Done,
                $"完成：{done.FileDst}  ·  {Helper.HumanSize(src.Size)} → {Helper.HumanSize(outSize)}");
            summary.Results.Add(new SessionFileResult
            {
                Name = src.Name, Path = src.Path, Outcome = SessionFileOutcome.Success,
                OutputPath = done.FileDst, Tier = tier,
                InputBytes = src.Size, OutputBytes = outSize, ElapsedMs = elapsed,
            });
            return;
        }

        if (done.Skipped || done.DstExists)
        {
            summary.Skipped++;
            // 与 prepare 阶段同一套判定：目标被本批早前文件占用时改用
            // destination_conflict_in_batch，并记下占用者，便于日志给出可执行结论。
            FfmpegTask.RefineDestinationConflict(done, batchOutputs);
            var reason = done.SkipReason ?? SkipReason.DestinationExists;
            log(SessionLogLevel.Warn, DescribeSkip(done, src));
            summary.Results.Add(new SessionFileResult
            {
                Name = src.Name, Path = src.Path, Outcome = SessionFileOutcome.Skipped,
                OutputPath = done.DstExistsPath ?? done.FileDst,
                Detail = reason,
                ConflictWith = done.ConflictedWithSource,
                InputBytes = src.Size, ElapsedMs = elapsed,
            });
            return;
        }

        summary.Failed++;
        var stage = done.FFmpegError is not null && done.FFmpegError.StartsWith("plan:", StringComparison.Ordinal)
            ? "plan"
            : "execute";
        log(SessionLogLevel.Error, $"失败[{stage}]：{done.FFmpegError ?? "未知错误"}");
        summary.Results.Add(new SessionFileResult
        {
            Name = src.Name, Path = src.Path, Outcome = SessionFileOutcome.Failed,
            Stage = stage, Detail = done.FFmpegError ?? "未知错误",
            Tier = tier, InputBytes = src.Size, ElapsedMs = elapsed,
        });
    }

    /// <summary>
    /// 生成跳过原因的可读描述。
    ///
    /// 关键点：始终给出「源 → 目标」两个路径。旧日志只打源路径 + destination_exists，
    /// 用户看到的是一条指向源文件的告警，却被告知"目标已存在"——无从判断是哪一个目标、
    /// 更无从判断是本批自撞还是旧文件占位。
    /// </summary>
    private static string DescribeSkip(TranscodeEntry task, ScanEntry src)
    {
        var target = task.DstExistsPath ?? task.FileDst;

        if (task.SkipReason == SkipReason.DestinationConflictInBatch)
        {
            var blocker = string.IsNullOrEmpty(task.ConflictedWithSource)
                ? "本批次早前的文件"
                : $"本批次的 {task.ConflictedWithSource}";
            return $"跳过[{task.SkipReason}] {src.Path} → 目标 {target} 已由{blocker}产出" +
                   "（同基名多容器文件映射到同一输出名；可用 --suffix 加 {srcExt} 区分）";
        }

        if (task.SkipReason == SkipReason.DestinationExists)
        {
            return $"跳过[{task.SkipReason}] {src.Path} → 目标已存在：{target}";
        }

        return string.IsNullOrEmpty(target)
            ? $"跳过[{task.SkipReason}] {src.Path}"
            : $"跳过[{task.SkipReason}] {src.Path} → {target}";
    }

    private static long FileSizeOf(string? path)    {
        try
        {
            return !string.IsNullOrEmpty(path) && File.Exists(path) ? new FileInfo(path).Length : 0;
        }
        catch
        {
            return 0;
        }
    }

    /// <summary>
    /// 转发 core 的输出。
    ///
    /// core 只发三类结构化行（[PREPARE]/[CMD]/[DONE]）与<b>原始 ffmpeg stderr</b>
    /// （<c>FfmpegRun.cs:423</c>，仅在「详细日志」时量很大）。
    /// 需求要求不显示 ffmpeg 常规 verbose，因此只对原始行做过滤——
    /// 结构化行是 core 自己的结论，必须完整保留。
    /// </summary>
    private void ForwardCoreLog(string line)
    {
        if (string.IsNullOrEmpty(line)) return;

        if (line.StartsWith("[DONE]", StringComparison.Ordinal))
        {
            log(SessionLogLevel.Done, line);
            return;
        }
        if (line.StartsWith("[CMD]", StringComparison.Ordinal))
        {
            log(SessionLogLevel.Cmd, line);
            return;
        }
        if (line.StartsWith("[PREPARE]", StringComparison.Ordinal))
        {
            log(SessionLogLevel.Info, line);
            return;
        }

        // 原始 ffmpeg 输出：默认只放行 error/warning，其余计数后丢弃；
        // 勾选「详细日志」时原样放行（keepEverything），与 core 的 -v 级别保持一致。
        // 否则用户勾选后几乎看不到额外输出，而统计块仍显示"已隐藏 N 行（勾选可全部显示）"。
        if (FfmpegLogFilter.Apply(line, keepEverything: verboseLog) is { } kept)
        {
            log(verboseLog ? SessionLogLevel.Info : SessionLogLevel.Warn, kept);
        }
        else
        {
            suppressedLines++;
        }
    }

    /// <summary>供测试直接驱动日志转发（不启动真实 ffmpeg），验证详细日志开关确实生效。</summary>
    internal void ForwardCoreLogForTest(string line) => ForwardCoreLog(line);

    private int suppressedLines;

    /// <summary>被隐藏的 ffmpeg 常规输出行数（供 UI 与统计块说明）。</summary>
    public int SuppressedLines => suppressedLines;

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
