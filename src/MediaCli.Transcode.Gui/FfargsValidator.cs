using System.Globalization;
using MediaCli.Transcode.Presets;

namespace MediaCli.Transcode.Gui;

/// <summary>
/// ffargs 输入框的解析与校验 —— GUI 侧的「可信」防线。
///
/// 存在的理由（均经 node 跑 JS 原版实测确认，见 docs/GUI-DESIGN 的「实测校准」一节）：
///
/// 1. <b>码率必须写裸 bps</b>：<c>applyFfargs</c> 对码率要求 <c>typeof value === "number"</c>，
///    而 <c>vb=3M</c> 会被解析成字符串 → <b>静默丢弃</b>。带单位的写法（"233k"/"4M"）只适用于
///    YAML 预设，不适用于 ffargs。
/// 2. <b>分隔符不一致</b>：JS 的 <c>arg_parser</c> 用 <c>;</c>/<c>:</c>/<c>#</c>，C# 的
///    <c>ParseFfargs</c> 用 <c>,</c>。本类同时接受 <c>,</c> 与 <c>;</c>，并统一归一为 C# 形式。
/// 3. <b>an/anime 是空操作</b>：别名表里有，但 <c>applyFfargs</c> 没有对应分支 —— 声明了却不生效。
///    动漫只能通过复选框（argv.anime）设置。
///
/// 因此：凡是不能生效的输入，一律<b>给出明确警告</b>，绝不静默吞掉。
/// </summary>
public static class FfargsValidator
{
    public sealed class Result
    {
        /// <summary>归一后的参数串（逗号分隔，可直接交给 core 的 ParseFfargs）。</summary>
        public required string Normalized { get; init; }
        /// <summary>不会生效或写法有问题的项（面向用户的说明）。</summary>
        public required IReadOnlyList<string> Warnings { get; init; }
        /// <summary>确认生效的项。</summary>
        public required IReadOnlyList<string> Accepted { get; init; }

        public bool HasWarnings => Warnings.Count > 0;
    }

    /// <summary>数值型键（core 要求 number 且 > 0）。</summary>
    private static readonly HashSet<string> NumericKeys = new(StringComparer.Ordinal)
    {
        "videoBitrate", "videoQuality", "audioBitrate", "audioQuality",
        "dimension", "speed", "framerate",
    };

    /// <summary>码率键：值必须为裸 bps 数字，带单位会被静默丢弃。</summary>
    private static readonly HashSet<string> BitrateKeys = new(StringComparer.Ordinal)
    {
        "videoBitrate", "audioBitrate",
    };

    /// <summary>字符串型键。</summary>
    private static readonly HashSet<string> StringKeys = new(StringComparer.Ordinal)
    {
        "videoCodec", "audioCodec", "prefix", "suffix", "preset", "metadata",
    };

    /// <summary>别名指向 "anime"，但 core 的 applyFfargs 无此分支 —— 声明了却不生效。</summary>
    private const string AnimeKey = "anime";

    /// <summary>解析并校验用户输入的 ffargs。</summary>
    public static Result Parse(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            return new Result { Normalized = "", Warnings = [], Accepted = [] };
        }

        var pairs = new List<string>();
        var warnings = new List<string>();
        var accepted = new List<string>();

        // 同时接受 "," 与 ";"：前者是 C# CLI 的写法，后者是 JS 的写法。
        foreach (var seg in raw.Split(new[] { ',', ';' },
                     StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var idx = seg.IndexOf('=');
            if (idx <= 0)
            {
                warnings.Add($"无法解析「{seg}」：应为 key=value 形式，已忽略。");
                continue;
            }

            var rawKey = seg[..idx].Trim();
            var value = seg[(idx + 1)..].Trim();
            if (rawKey.Length == 0)
            {
                warnings.Add($"无法解析「{seg}」：键名为空，已忽略。");
                continue;
            }
            if (value.Length == 0)
            {
                warnings.Add($"「{rawKey}」的值为空，已忽略。");
                continue;
            }

            var key = FFmpegPresets.ArgAliasesView.TryGetValue(rawKey, out var normalized)
                ? normalized
                : rawKey;

            if (key == AnimeKey)
            {
                warnings.Add($"「{rawKey}」不会生效（core 的 ffargs 未实现该键），请改用「动漫模式」复选框。");
                continue;
            }

            if (NumericKeys.Contains(key))
            {
                if (!double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var num))
                {
                    warnings.Add(BitrateKeys.Contains(key)
                        ? $"「{rawKey}={value}」带单位不会生效：码率请写裸 bps，例如 {rawKey}=3000000（即 3M）。"
                        : $"「{rawKey}={value}」不是数字，已忽略。");
                    continue;
                }
                if (num <= 0)
                {
                    warnings.Add($"「{rawKey}={value}」必须大于 0，已忽略。");
                    continue;
                }

                // 归一为无单位的数字串，确保 core 侧按 number 处理而不被丢弃
                pairs.Add($"{rawKey}={num.ToString(CultureInfo.InvariantCulture)}");
                accepted.Add($"{rawKey}={value}");
                continue;
            }

            if (StringKeys.Contains(key))
            {
                pairs.Add($"{rawKey}={value}");
                accepted.Add($"{rawKey}={value}");
                continue;
            }

            warnings.Add($"未知参数键「{rawKey}」，已忽略。可用：{AvailableKeys()}");
        }

        return new Result
        {
            Normalized = string.Join(",", pairs),
            Warnings = warnings,
            Accepted = accepted,
        };
    }

    private static string AvailableKeys() =>
        string.Join("/", FFmpegPresets.ArgAliasesView.Keys.OrderBy(k => k, StringComparer.Ordinal));
}
