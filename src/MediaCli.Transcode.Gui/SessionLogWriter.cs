using System.Text;

namespace MediaCli.Transcode.Gui;

/// <summary>
/// 会话日志落盘（需求：任务完成后显示日志文件位置；可同步一份到输出目录）。
///
/// 设计要点：
/// - <b>始终写一份到临时目录</b>，任务结束后把路径报给用户，便于出问题时取证。
/// - <b>可选同步一份到输出目录</b>（用户勾选），方便随产物一起归档。
/// - 写盘失败<b>绝不影响转码</b>：日志是辅助产物，不能因为磁盘只读或路径非法
///   就让转码任务失败。所有 IO 异常都被吞掉并降级为无日志。
/// - 只做追加写，不做并发保护之外的复杂逻辑：生产者来自 core 的回调线程，
///   因此用锁串行化，代价远低于丢日志。
/// </summary>
public sealed class SessionLogWriter : IDisposable
{
    private readonly object gate = new();
    private readonly List<string> lines = [];
    private readonly StringBuilder buffer = new();

    private StreamWriter? tempWriter;
    private string? tempPath;
    private bool disposed;

    /// <summary>临时日志文件路径（写盘成功时非空）。</summary>
    public string? TempPath { get { lock (gate) return tempPath; } }

    /// <summary>同步到输出目录后的路径（未同步或失败时为 null）。</summary>
    public string? SyncedPath { get; private set; }

    /// <summary>写盘过程中的错误（供界面如实说明，而不是假装日志已生成）。</summary>
    public string? WriteError { get; private set; }

    /// <summary>已记录的行数。</summary>
    public int LineCount { get { lock (gate) return lines.Count; } }

    public SessionLogWriter()
    {
        try
        {
            var dir = Path.Combine(Path.GetTempPath(), "mediac-gui-logs");
            Directory.CreateDirectory(dir);
            // 文件名必须带唯一后缀：只精确到秒时，同一秒内创建两个 writer
            // （例如连续两次运行）会撞名，后一个因文件被占用而抛 IOException。
            tempPath = Path.Combine(dir, $"mediac-{Stamp()}.log");
            // FileShare.ReadWrite：允许在转码过程中用编辑器打开该日志查看，
            // 也让测试能直接读取内容。默认的独占打开会让"边跑边看日志"直接失败。
            var stream = new FileStream(
                tempPath, FileMode.Create, FileAccess.Write, FileShare.ReadWrite);
            tempWriter = new StreamWriter(stream, Encoding.UTF8) { AutoFlush = true };
        }
        catch (Exception ex)
        {
            WriteError = ex.Message;
            tempPath = null;
            tempWriter = null;
        }
    }

    /// <summary>时间戳 + 短随机后缀，保证同一秒内多次调用也不撞名。</summary>
    private static string Stamp() =>
        $"{DateTime.Now:yyyyMMdd-HHmmss}-{Guid.NewGuid():N}"[..23];

    /// <summary>线程安全：可从任意线程调用。</summary>
    public void Write(SessionLogLevel level, string text, DateTime at)
    {
        if (disposed || string.IsNullOrEmpty(text)) return;

        var stamp = level switch
        {
            SessionLogLevel.Cmd => "",
            SessionLogLevel.Warn => "! ",
            SessionLogLevel.Error => "× ",
            SessionLogLevel.Done => "✓ ",
            _ => "  ",
        };

        lock (gate)
        {
            // 多行内容逐行记录，保持文件可读；纯空白行不入库
            // （否则日志里会出现只有时间戳的空行，也会虚增行数统计）
            foreach (var raw in text.Split('\n'))
            {
                var line = raw.TrimEnd('\r');
                if (string.IsNullOrWhiteSpace(line)) continue;
                var formatted = $"[{at:yyyy-MM-dd HH:mm:ss}] {stamp}{line}";
                lines.Add(formatted);
                buffer.AppendLine(formatted);
            }

            try
            {
                tempWriter?.Write(buffer.ToString());
                buffer.Clear();
            }
            catch (Exception ex)
            {
                // 记录一次即可，避免反复覆盖首个真实原因
                WriteError ??= ex.Message;
            }
        }
    }

    /// <summary>
    /// 把日志同步一份到 <paramref name="outputDir"/>。
    /// 返回同步后的路径；失败或未请求时返回 null，并通过 <see cref="WriteError"/> 说明。
    /// </summary>
    public string? SyncTo(string? outputDir)
    {
        if (string.IsNullOrWhiteSpace(outputDir)) return null;

        lock (gate)
        {
            try
            {
                Directory.CreateDirectory(outputDir);
                // 同样加唯一后缀，避免同一秒内两次同步撞名
                var name = $"mediac-transcode-{DateTime.Now:yyyyMMdd-HHmmss}-{Guid.NewGuid():N}"[..(24 + 6)] + ".log";
                var target = Path.Combine(outputDir, name);
                File.WriteAllText(target, string.Join(Environment.NewLine, lines), Encoding.UTF8);
                SyncedPath = target;
                return target;
            }
            catch (Exception ex)
            {
                WriteError ??= ex.Message;
                return null;
            }
        }
    }

    /// <summary>全部日志文本（用于同步或界面导出）。</summary>
    public string GetAllText()
    {
        lock (gate) return string.Join(Environment.NewLine, lines);
    }

    public void Dispose()
    {
        lock (gate)
        {
            if (disposed) return;
            disposed = true;
            try { tempWriter?.Dispose(); } catch { /* 关闭失败不影响结论 */ }
            tempWriter = null;
        }
    }
}
