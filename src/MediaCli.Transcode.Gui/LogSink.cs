using System.Collections.Concurrent;

namespace MediaCli.Transcode.Gui;

/// <summary>一条日志。</summary>
public sealed record LogLine(SessionLogLevel Level, string Text, DateTime At);

/// <summary>
/// 日志汇聚与批量刷新。
///
/// 为什么需要它：core 的 <c>RunOptions.OnLog</c> 从 <c>ErrorDataReceived</c> 的
/// 线程池线程触发。若在回调里直接 <c>Control.Invoke</c> 同步等待 UI 线程，
/// 会造成卡顿甚至死锁；勾选「详细日志」后回调频率暴涨，问题更明显。
///
/// 做法：生产者只做无锁入队（不等待），UI 侧用定时器按批 drain。
/// 三重防护避免内存被撑爆：队列上限 + 显示行数上限 + 单批条数上限。
/// </summary>
public sealed class LogSink
{
    /// <summary>待处理队列上限：超出则丢弃最旧的，保证生产者永不阻塞。</summary>
    private const int MaxPending = 20_000;

    /// <summary>RichTextBox 保留的最大行数。</summary>
    private const int MaxDisplayLines = 5_000;

    /// <summary>单次刷新最多消费的条数，避免一次 drain 卡住 UI。</summary>
    private const int MaxDrainPerTick = 400;

    private readonly ConcurrentQueue<LogLine> pending = new();
    private long pendingCount;
    private long dropped;

    public int DisplayLineCount { get; private set; }

    /// <summary>累计丢弃条数（超限时），UI 应提示用户。</summary>
    public long DroppedCount => Interlocked.Read(ref dropped);

    /// <summary>线程安全：可从任意线程调用（core 的回调线程 / UI 线程）。</summary>
    public void Write(SessionLogLevel level, string text)
    {
        if (string.IsNullOrEmpty(text)) return;

        // 多行文本拆成多条，便于逐行着色与限流
        var lines = text.Split('\n');
        foreach (var raw in lines)
        {
            var line = raw.TrimEnd('\r');
            if (line.Length == 0) continue;
            pending.Enqueue(new LogLine(level, line, DateTime.Now));
            var count = Interlocked.Increment(ref pendingCount);
            if (count <= MaxPending) continue;

            // 超限：丢最旧的，维持队列有界
            if (pending.TryDequeue(out _))
            {
                Interlocked.Decrement(ref pendingCount);
                Interlocked.Increment(ref dropped);
            }
        }
    }

    /// <summary>UI 线程调用：取出本批待显示日志（至多 <see cref="MaxDrainPerTick"/> 条）。</summary>
    public List<LogLine> Drain()
    {
        var batch = new List<LogLine>();
        while (batch.Count < MaxDrainPerTick && pending.TryDequeue(out var line))
        {
            Interlocked.Decrement(ref pendingCount);
            batch.Add(line);
        }
        if (batch.Count > 0)
        {
            DisplayLineCount += batch.Count;
        }
        return batch;
    }

    /// <summary>清空（UI 的"清空日志"按钮）。</summary>
    public void Clear()
    {
        while (pending.TryDequeue(out _))
        {
            Interlocked.Decrement(ref pendingCount);
        }
        DisplayLineCount = 0;
        Interlocked.Exchange(ref dropped, 0);
    }

    public static int DisplayLimit => MaxDisplayLines;
}
