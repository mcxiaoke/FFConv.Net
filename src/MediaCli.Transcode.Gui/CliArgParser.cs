using System.Globalization;
using System.Text;

namespace MediaCli.Transcode.Gui;

/// <summary>解析出的一项参数。</summary>
public sealed record ParsedCliArg(CliOption Option, string? Value, string Raw);

/// <summary>CLI 参数解析结果。</summary>
public sealed class CliArgsParseResult
{
    /// <summary>成功解析且 GUI 支持的参数（保持输入顺序）。</summary>
    public required IReadOnlyList<ParsedCliArg> Accepted { get; init; }
    /// <summary>面向用户的告警（未知参数、不支持、取值非法）。</summary>
    public required IReadOnlyList<string> Warnings { get; init; }
    /// <summary>已解析的原始键值对（供映射层消费）。</summary>
    public required IReadOnlyDictionary<string, string> Values { get; init; }

    public bool HasWarnings => Warnings.Count > 0;

    public bool Has(string name) => Values.ContainsKey(name);

    public string? Get(string name) => Values.TryGetValue(name, out var v) ? v : null;
}

/// <summary>
/// mediac CLI 风格参数的解析器。
///
/// 支持 mediac <c>cmd_ffmpeg</c> 的参数写法：
/// <code>
/// --video-bitrate 3M --video-quality 23 -O --include foo
/// --video-bitrate=3M            （等号写法）
/// --metadata "title=My Video"   （引号包裹的值，可含空格）
/// -O -E "shana|.m4a"            （短别名与组合写法）
/// </code>
///
/// 设计原则：<b>凡是不能生效的输入都必须告警</b>。
/// 未知参数、GUI 不支持的能力（如 --jobs）、取值非法（如 --speed 9）都产生明确提示，
/// 绝不静默忽略——静默忽略会让用户以为参数已经生效。
/// </summary>
public static class CliArgParser
{
    /// <summary>解析参数串。</summary>
    public static CliArgsParseResult Parse(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            return new CliArgsParseResult
            {
                Accepted = [], Warnings = [], Values = new Dictionary<string, string>(),
            };
        }

        var tokens = Tokenize(raw);
        var accepted = new List<ParsedCliArg>();
        var warnings = new List<string>();
        var values = new Dictionary<string, string>(StringComparer.Ordinal);

        for (var i = 0; i < tokens.Count; i++)
        {
            var token = tokens[i];

            if (!token.StartsWith('-'))
            {
                warnings.Add($"无法识别的片段「{token}」：参数应以 -- 开头，已忽略。");
                continue;
            }

            // 拆出键与内联值（--key=value）
            var body = token.TrimStart('-');
            if (body.Length == 0)
            {
                warnings.Add($"无法识别的片段「{token}」：参数名称为空，已忽略。");
                continue;
            }

            string? inlineValue = null;
            var eq = body.IndexOf('=');
            if (eq >= 0)
            {
                inlineValue = body[(eq + 1)..];
                body = body[..eq];
            }

            var option = CliOptions.Resolve(body);
            if (option is null)
            {
                // 未知参数：把它后面紧跟的取值一并跳过。
                // 否则 `--unknown value` 会产生两条告警（未知参数 + 无法识别片段 value），
                // 而 value 本来就是这个未知参数的取值，重复提示只会让用户困惑。
                if (inlineValue is null && i + 1 < tokens.Count && !IsOptionToken(tokens[i + 1]))
                {
                    i++;
                }
                warnings.Add(
                    $"未知参数「{token}」，已忽略。可用参数见「使用说明」按钮，" +
                    $"或用 --ffargs 传复合参数。");
                continue;
            }

            string? value;
            if (option.Kind == CliValueKind.Flag)
            {
                value = inlineValue is null ? "true" : NormalizeFlag(inlineValue, option, warnings);
                if (value is null) continue;
            }
            else
            {
                if (inlineValue is not null)
                {
                    value = inlineValue;
                }
                else if (i + 1 < tokens.Count && !IsOptionToken(tokens[i + 1]))
                {
                    value = tokens[++i];
                }
                else
                {
                    warnings.Add($"参数「{option.Name}」缺少取值，已忽略。{option.Description}");
                    continue;
                }
            }

            if (!option.Supported)
            {
                warnings.Add($"参数「{option.Name}」在当前 GUI 中不生效：{option.Reason}。已忽略。");
                continue;
            }

            if (!Validate(option, value, warnings)) continue;

            // 重复参数：后者覆盖前者（与命令行习惯一致），并提示
            if (values.ContainsKey(option.Name))
            {
                warnings.Add($"参数「{option.Name}」重复出现，以最后一次为准。");
            }

            values[option.Name] = value;
            accepted.Add(new ParsedCliArg(option, value, token));
        }

        return new CliArgsParseResult
        {
            Accepted = accepted,
            Warnings = warnings,
            Values = values,
        };
    }

    /// <summary>取值是否合法；非法时追加告警并返回 false。</summary>
    private static bool Validate(CliOption option, string value, List<string> warnings)
    {
        switch (option.Kind)
        {
            case CliValueKind.Number:
                if (!double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out _))
                {
                    warnings.Add($"参数「{option.Name}」需要数字，收到「{value}」，已忽略。");
                    return false;
                }
                break;

            case CliValueKind.Bitrate:
                if (!IsValidBitrate(value))
                {
                    warnings.Add(
                        $"参数「{option.Name}」的取值「{value}」不是合法码率，" +
                        "请写纯数字(bps) 或带 k/m/g 单位（如 3000000、3M、800k）。已忽略。");
                    return false;
                }
                break;

            case CliValueKind.Choice:
                if (option.Choices is not null &&
                    !option.Choices.Contains(value, StringComparer.OrdinalIgnoreCase))
                {
                    warnings.Add(
                        $"参数「{option.Name}」的取值「{value}」不合法，" +
                        $"可选：{string.Join(" / ", option.Choices)}。已忽略。");
                    return false;
                }
                break;
        }

        // 逐项的范围约束（core 侧也会拒绝，但提前给出可读提示）
        if (option.Name == "speed" &&
            double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var sp) &&
            sp != 0 && (sp < 0.5 || sp > 2.0))
        {
            // 0 是 mediac 文档化的「不变速」取值（也是 CLI 默认值），必须放行；
            // 只拦真正越界的倍率。
            warnings.Add($"参数「speed」的取值 {sp} 超出范围，应为 0.5–2.0（0 表示不变速）。已忽略。");
            return false;
        }

        return true;
    }

    /// <summary>码率写法：纯数字或 数字+k/m/g（与 core 的 parseBitrate 一致）。</summary>
    private static bool IsValidBitrate(string value)
    {
        var s = value.Trim().ToLowerInvariant();
        if (s.Length == 0) return false;
        var i = 0;
        var digits = 0;
        while (i < s.Length && (char.IsAsciiDigit(s[i]) || s[i] == '.')) { i++; digits++; }
        if (digits == 0) return false;
        if (i == s.Length) return true;
        if (s[i] is 'k' or 'm' or 'g') return i == s.Length - 1;
        return false;
    }

    private static string? NormalizeFlag(string raw, CliOption option, List<string> warnings)
    {
        var v = raw.Trim().ToLowerInvariant();
        if (v is "true" or "1" or "yes" or "on") return "true";
        if (v is "false" or "0" or "no" or "off") return "false";
        warnings.Add($"开关「{option.Name}」的取值「{raw}」无法识别（用 true/false），已忽略。");
        return null;
    }

    /// <summary>判断 token 是否是一个新的参数（用于区分「缺取值」与「取值恰好以 - 开头」）。</summary>
    private static bool IsOptionToken(string token)
    {
        if (token.Length < 2 || token[0] != '-') return false;
        var body = token.TrimStart('-');
        if (body.Length == 0) return false;
        var eq = body.IndexOf('=');
        var name = eq >= 0 ? body[..eq] : body;
        return CliOptions.Resolve(name) is not null;
    }

    /// <summary>
    /// 分词：按空白切分，支持单/双引号包裹的值（值内可含空格）。
    /// 例：<c>--metadata "title=My Video"</c> → ["--metadata", "title=My Video"]
    ///
    /// public 而非 internal：测试需要直接验证分词边界（引号、连续空白）。
    /// </summary>
    public static List<string> Tokenize(string raw)
    {
        var tokens = new List<string>();
        var sb = new StringBuilder();
        var quote = '\0';

        foreach (var ch in raw)
        {
            if (quote != '\0')
            {
                if (ch == quote) quote = '\0';
                else sb.Append(ch);
                continue;
            }

            if (ch is '"' or '\'')
            {
                quote = ch;
                continue;
            }

            if (char.IsWhiteSpace(ch))
            {
                if (sb.Length > 0) { tokens.Add(sb.ToString()); sb.Clear(); }
                continue;
            }

            sb.Append(ch);
        }

        if (sb.Length > 0) tokens.Add(sb.ToString());
        return tokens;
    }
}
