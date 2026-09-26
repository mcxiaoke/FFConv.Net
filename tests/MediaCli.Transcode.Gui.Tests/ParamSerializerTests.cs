using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using Xunit;

namespace MediaCli.Transcode.Gui.Tests;

public class ParamSerializerTests
{
	private static Dictionary<string, string?> Values(params (string Name, string? Value)[] items)
	{
		Dictionary<string, string?> dictionary = new Dictionary<string, string?>(StringComparer.Ordinal);
		for (int i = 0; i < items.Length; i++)
		{
			(string Name, string? Value) tuple = items[i];
			string item = tuple.Name;
			string? item2 = tuple.Value;
			dictionary[item] = item2;
		}
		return dictionary;
	}

	[Fact]
	public void Serialize_Empty_ReturnsEmptyText()
	{
		SerializeResult serializeResult = ParamSerializer.Serialize(Values());
		Assert.Equal("", serializeResult.Text);
		Assert.Empty((IEnumerable)serializeResult.Skipped);
	}

	[Fact]
	public void Serialize_SortedByName_ForDeterministicOutput()
	{
		SerializeResult serializeResult = ParamSerializer.Serialize(Values(("video-quality", "23"), ("audio-bitrate", "192k"), ("include", "1080")));
		Assert.Equal("--audio-bitrate=192k --include=1080 --video-quality=23", serializeResult.Text);
	}

	[Fact]
	public void Serialize_Flag_WritesNameOnly()
	{
		SerializeResult serializeResult = ParamSerializer.Serialize(Values(("video-copy", "true")));
		Assert.Equal("--video-copy", serializeResult.Text);
	}

	[Fact]
	public void Serialize_FlagFalse_IsOmitted()
	{
		SerializeResult serializeResult = ParamSerializer.Serialize(Values(("video-copy", "false")));
		Assert.Equal("", serializeResult.Text);
	}

	[Fact]
	public void Serialize_BlankValue_IsOmitted()
	{
		SerializeResult serializeResult = ParamSerializer.Serialize(Values(("suffix", "  "), ("prefix", null)));
		Assert.Equal("", serializeResult.Text);
	}

	[Fact]
	public void Serialize_UnknownName_IsIgnored()
	{
		SerializeResult serializeResult = ParamSerializer.Serialize(Values(("not-a-param", "x")));
		Assert.Equal("", serializeResult.Text);
	}

	[Fact]
	public void Serialize_ValueWithSpace_IsQuoted()
	{
		SerializeResult serializeResult = ParamSerializer.Serialize(Values(("metadata", "title=My Video")));
		Assert.Equal("--metadata=\"title=My Video\"", serializeResult.Text);
	}

	[Fact]
	public void Serialize_ValueStartingWithDash_IsQuoted()
	{
		SerializeResult serializeResult = ParamSerializer.Serialize(Values(("prefix", "-x")));
		Assert.Equal("--prefix=\"-x\"", serializeResult.Text);
	}

	[Fact]
	public void Serialize_ValueWithDoubleQuote_UsesSingleQuote()
	{
		SerializeResult serializeResult = ParamSerializer.Serialize(Values(("metadata", "comment=say \"hi\"")));
		Assert.Equal("--metadata='comment=say \"hi\"'", serializeResult.Text);
	}

	[Fact]
	public void Serialize_ValueWithBothQuotes_IsSkipped()
	{
		SerializeResult serializeResult = ParamSerializer.Serialize(Values(("metadata", "a'b\"c")));
		Assert.Equal("", serializeResult.Text);
		Assert.Single<string>((IEnumerable<string>)serializeResult.Skipped);
		Assert.Contains("无法写入参数框", serializeResult.Skipped[0]);
	}

	[Theory]
	[InlineData(new object[] { "video-quality", "23" })]
	[InlineData(new object[] { "video-bitrate", "3M" })]
	[InlineData(new object[] { "metadata", "title=My Video;comment=hi" })]
	[InlineData(new object[] { "prefix", "-x" })]
	[InlineData(new object[] { "suffix", "_{preset}" })]
	[InlineData(new object[] { "regex", ".*\\.mkv$" })]
	[InlineData(new object[] { "metadata", "comment=say \"hi\"" })]
	public void RoundTrip_SingleValue_IsStable(string name, string value)
	{
		string text = ParamSerializer.Serialize(Values((name, value))).Text;
		DeserializeResult deserializeResult = ParamSerializer.Deserialize(text);
		Assert.Empty((IEnumerable)deserializeResult.Warnings);
		Assert.Equal(value, deserializeResult.Values[name]);
	}

	[Fact]
	public void RoundTrip_MixedKinds_PreservesAll()
	{
		Dictionary<string, string?> values = Values(("video-quality", "23"), ("video-copy", "true"), ("metadata", "title=My Video"), ("include", "1080"), ("start", "2"));
		string text = ParamSerializer.Serialize(values).Text;
		DeserializeResult deserializeResult = ParamSerializer.Deserialize(text);
		Assert.Empty((IEnumerable)deserializeResult.Warnings);
		Assert.Equal<int>(5, deserializeResult.Values.Count);
		Assert.Equal("23", deserializeResult.Values["video-quality"]);
		Assert.Equal("true", deserializeResult.Values["video-copy"]);
		Assert.Equal("title=My Video", deserializeResult.Values["metadata"]);
		Assert.Equal("1080", deserializeResult.Values["include"]);
		Assert.Equal("2", deserializeResult.Values["start"]);
	}

	[Fact]
	public void RoundTrip_Serialize_IsIdempotent()
	{
		Dictionary<string, string?> values = Values(("video-quality", "23"), ("metadata", "title=My Video"), ("video-copy", "true"));
		string text = ParamSerializer.Serialize(values).Text;
		DeserializeResult deserializeResult = ParamSerializer.Deserialize(text);
		string text2 = ParamSerializer.Serialize(deserializeResult.Values.ToDictionary(kv => kv.Key, kv => (string?)kv.Value, StringComparer.Ordinal)).Text;
		Assert.Equal(text, text2);
	}

	[Fact]
	public void Deserialize_NormalizesAliasToCanonicalName()
	{
		DeserializeResult deserializeResult = ParamSerializer.Deserialize("-O --vb 3M");
		Assert.True(deserializeResult.Values.ContainsKey("override"));
		Assert.True(deserializeResult.Values.ContainsKey("video-bitrate"));
		Assert.False(deserializeResult.Values.ContainsKey("vb"));
	}

	[Fact]
	public void Deserialize_UnsupportedParam_IsDroppedWithWarning()
	{
		DeserializeResult deserializeResult = ParamSerializer.Deserialize("--jobs 4");
		Assert.Empty((IEnumerable)deserializeResult.Values);
		Assert.Single<string>((IEnumerable<string>)deserializeResult.Warnings);
	}

	[Fact]
	public void Deserialize_UnknownParam_IsDroppedWithWarning()
	{
		DeserializeResult deserializeResult = ParamSerializer.Deserialize("--no-such-thing 1");
		Assert.Empty((IEnumerable)deserializeResult.Values);
		Assert.Single<string>((IEnumerable<string>)deserializeResult.Warnings);
	}
}
