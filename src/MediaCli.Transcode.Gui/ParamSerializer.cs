using System;
using System.Collections.Generic;
using System.Linq;

namespace MediaCli.Transcode.Gui;

public sealed record SerializeResult(string Text, IReadOnlyList<string> Skipped);
public sealed record DeserializeResult(IReadOnlyDictionary<string, string> Values, IReadOnlyList<string> Warnings);

public static class ParamSerializer
{
	public static SerializeResult Serialize(IReadOnlyDictionary<string, string?> values)
	{
		List<string> list = new List<string>();
		List<string> list2 = new List<string>();
		foreach (string item in values.Keys.OrderBy((string n) => n, StringComparer.Ordinal))
		{
			CliOption? cliOption = CliOptions.Resolve(item);
			if (cliOption == null)
			{
				continue;
			}
			string? text = values[item];
			if (cliOption.Kind == CliValueKind.Flag)
			{
				if (IsTruthy(text))
				{
					list.Add("--" + item);
				}
			}
			else if (!string.IsNullOrWhiteSpace(text))
			{
				if (TryFormat(item, text, out string token))
				{
					list.Add(token);
				}
				else
				{
					list2.Add("参数「--" + item + "」的取值同时包含单引号与双引号，无法写入参数框，已跳过。");
				}
			}
		}
		return new SerializeResult(string.Join(" ", list), list2);
	}

	public static DeserializeResult Deserialize(string? text)
	{
		CliArgsParseResult cliArgsParseResult = CliArgParser.Parse(text);
		return new DeserializeResult(cliArgsParseResult.Values, cliArgsParseResult.Warnings);
	}

	private static bool IsTruthy(string? raw)
	{
		if (!string.IsNullOrWhiteSpace(raw))
		{
			return !string.Equals(raw, "false", StringComparison.OrdinalIgnoreCase);
		}
		return false;
	}

	private static bool TryFormat(string name, string value, out string token)
	{
		if (!value.Any(char.IsWhiteSpace) && !value.StartsWith('-') && !value.Contains('"') && !value.Contains('\''))
		{
			token = "--" + name + "=" + value;
			return true;
		}
		bool flag = value.Contains('"');
		bool flag2 = value.Contains('\'');
		if (flag & flag2)
		{
			token = "";
			return false;
		}
		char value2 = (flag ? '\'' : '"');
		token = $"--{name}={value2}{value}{value2}";
		return true;
	}
}
