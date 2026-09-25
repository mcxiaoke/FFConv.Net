using System.Text;
using MediaCli.Transcode.Support;

namespace MediaCli.Transcode.Gui;

/// <summary>
/// 任务结束后的统计块（需求：日志末尾显示统计信息，含日志文件位置、输入/输出目录、
/// 总耗时、输入输出文件大小）。
///
/// 独立成类以便单元测试直接断言文本，无需驱动界面。
/// 所有数字都来自实际观测（<see cref="SessionSummary"/> 由编排层累计），
/// 无法获得的项如实标注「—」，不编造。
/// </summary>
public static class SessionReport
{
    /// <summary>把毫秒格式化为易读时长。</summary>
    public static string FormatDuration(long ms)
    {
        if (ms < 0) return "—";
        var t = TimeSpan.FromMilliseconds(ms);
        if (t.TotalSeconds < 60) return $"{t.TotalSeconds:0.0} 秒";
        if (t.TotalMinutes < 60) return $"{(int)t.TotalMinutes} 分 {t.Seconds} 秒";
        return $"{(int)t.TotalHours} 时 {t.Minutes} 分 {t.Seconds} 秒";
    }

    /// <summary>
    /// 生成统计块文本。
    /// </summary>
    /// <param name="summary">编排层累计的统计。</param>
    /// <param name="inputDir">输入目录（多个时给出首个并标注数量）。</param>
    /// <param name="outputDir">输出目录（未指定时说明输出到源目录）。</param>
    /// <param name="logPath">临时日志文件路径。</param>
    /// <param name="syncedPath">同步到输出目录的日志路径。</param>
    /// <param name="logError">日志写盘错误（如有）。</param>
    public static string Build(
        SessionSummary summary,
        string? inputDir,
        string? outputDir,
        string? logPath,
        string? syncedPath,
        string? logError = null)
    {
        var sb = new StringBuilder();
        sb.AppendLine();
        sb.AppendLine("════════════ 任务统计 ════════════");

        sb.AppendLine($"输入目录：{Or(inputDir, "（未指定）")}");
        sb.AppendLine($"输出目录：{Or(outputDir, "（未指定，输出到源文件目录）")}");
        sb.AppendLine();

        // 结果计数：只列出出现过的类别，避免一堆 0 干扰阅读
        var counts = new List<string> { $"共 {summary.Total} 个" };
        if (summary.Success > 0) counts.Add($"成功 {summary.Success}");
        if (summary.Failed > 0) counts.Add($"失败 {summary.Failed}");
        if (summary.Skipped > 0) counts.Add($"跳过 {summary.Skipped}");
        if (summary.Preview > 0) counts.Add($"预览 {summary.Preview}");
        if (summary.Cancelled > 0) counts.Add($"取消 {summary.Cancelled}");
        sb.AppendLine($"处理结果：{string.Join("  ·  ", counts)}");

        sb.AppendLine($"总耗时：{FormatDuration(summary.ElapsedMs)}" + AverageHint(summary));
        sb.AppendLine($"输入大小：{SizeText(summary.InputBytes)}");
        sb.AppendLine($"输出大小：{OutputSizeText(summary)}");

        if (summary.SuppressedLines > 0)
        {
            sb.AppendLine(
                $"已隐藏 ffmpeg 常规输出 {summary.SuppressedLines} 行（勾选「详细日志」可全部显示）");
        }

        sb.AppendLine();
        sb.AppendLine($"日志文件：{Or(logPath, "（未能创建）")}");
        if (!string.IsNullOrWhiteSpace(syncedPath))
        {
            sb.AppendLine($"同步副本：{syncedPath}");
        }
        if (!string.IsNullOrWhiteSpace(logError))
        {
            sb.AppendLine($"日志写盘告警：{logError}");
        }

        // 失败清单：统计块要能直接指向问题文件，省去在长日志里翻找
        var failures = summary.Results.Where(r => r.Outcome == SessionFileOutcome.Failed).ToList();
        if (failures.Count > 0)
        {
            sb.AppendLine();
            sb.AppendLine("失败文件：");
            foreach (var f in failures)
            {
                var stage = string.IsNullOrEmpty(f.Stage) ? "" : $"[{f.Stage}] ";
                sb.AppendLine($"  · {f.Name}  {stage}{f.Detail}");
            }
        }

        sb.AppendLine("═════════════════════════════════");
        return sb.ToString();
    }

    private static string AverageHint(SessionSummary s)
    {
        var processed = s.Success + s.Failed;
        if (processed <= 0 || s.ElapsedMs <= 0) return "";
        return $"（平均 {s.ElapsedMs / (double)processed / 1000:0.0} 秒/文件）";
    }

    private static string OutputSizeText(SessionSummary s)
    {
        if (s.OutputCount == 0)
        {
            return s.Preview > 0 ? "（预览模式未产出文件）" : "—";
        }

        var text = SizeText(s.OutputBytes);
        if (s.InputBytes > 0 && s.OutputBytes > 0)
        {
            var ratio = s.OutputBytes * 100.0 / s.InputBytes;
            text += $"（为输入的 {ratio:0.0}%）";
        }
        return text;
    }

    private static string SizeText(long bytes) =>
        bytes > 0 ? Helper.HumanSize(bytes) : "—";

    private static string Or(string? value, string fallback) =>
        string.IsNullOrWhiteSpace(value) ? fallback : value;
}
