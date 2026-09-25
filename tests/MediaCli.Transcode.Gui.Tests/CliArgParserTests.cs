using MediaCli.Transcode.Gui;
using Xunit;

namespace MediaCli.Transcode.Gui.Tests;

/// <summary>
/// mediac CLI 风格参数解析的测试。
///
/// 核心承诺：<b>凡是不能生效的输入都必须告警</b>。
/// 未知参数、GUI 不支持的能力、取值非法都产生明确提示，
/// 绝不静默忽略——静默忽略会让用户以为参数已经生效。
/// </summary>
public class CliArgParserTests
{
    // ------------------------------------------------------------------
    // 基本解析
    // ------------------------------------------------------------------

    [Fact]
    public void Parse_SpaceSeparated()
    {
        var r = CliArgParser.Parse("--video-bitrate 3M --video-quality 23");
        Assert.Empty(r.Warnings);
        Assert.Equal("3M", r.Get("video-bitrate"));
        Assert.Equal("23", r.Get("video-quality"));
    }

    [Fact]
    public void Parse_EqualsForm()
    {
        var r = CliArgParser.Parse("--video-bitrate=3M --include=foo");
        Assert.Empty(r.Warnings);
        Assert.Equal("3M", r.Get("video-bitrate"));
        Assert.Equal("foo", r.Get("include"));
    }

    /// <summary>短别名必须与 mediac 一致（vb / vq / O / I / E）。</summary>
    [Theory]
    [InlineData("--vb 3000000", "video-bitrate", "3000000")]
    [InlineData("--vq 23", "video-quality", "23")]
    [InlineData("--ab 192k", "audio-bitrate", "192k")]
    [InlineData("--aq 3", "audio-quality", "3")]
    [InlineData("--vc libx265", "video-codec", "libx265")]
    [InlineData("--dm 1920", "dimension", "1920")]
    [InlineData("--sp 1.5", "speed", "1.5")]
    [InlineData("--fps 30", "fps", "30")]
    [InlineData("--px PRE_", "prefix", "PRE_")]
    [InlineData("--sx _END", "suffix", "_END")]
    [InlineData("--I 1080", "include", "1080")]
    [InlineData("--E sample", "exclude", "sample")]
    public void Parse_Aliases(string input, string expectedKey, string expectedValue)
    {
        var r = CliArgParser.Parse(input);
        Assert.Empty(r.Warnings);
        Assert.Equal(expectedValue, r.Get(expectedKey));
    }

    [Theory]
    [InlineData("-O", "override")]
    [InlineData("--override", "override")]
    [InlineData("--strict", "strict")]
    [InlineData("--anime", "anime")]
    public void Parse_Flags(string input, string expectedKey)
    {
        var r = CliArgParser.Parse(input);
        Assert.Empty(r.Warnings);
        Assert.Equal("true", r.Get(expectedKey));
    }

    /// <summary>引号包裹的值可含空格（--metadata "title=My Video"）。</summary>
    [Fact]
    public void Parse_QuotedValueWithSpaces()
    {
        var r = CliArgParser.Parse("--metadata \"title=My Video;comment=hi\"");
        Assert.Empty(r.Warnings);
        Assert.Equal("title=My Video;comment=hi", r.Get("metadata"));
    }

    [Fact]
    public void Tokenize_HandlesQuotesAndWhitespace()
    {
        var tokens = CliArgParser.Tokenize("--a 1  --b \"x y\"  'z w'");
        Assert.Equal(["--a", "1", "--b", "x y", "z w"], tokens);
    }

    // ------------------------------------------------------------------
    // 告警：不能生效的输入必须说出来
    // ------------------------------------------------------------------

    [Fact]
    public void Parse_UnknownOption_Warns()
    {
        var r = CliArgParser.Parse("--nonexistent 1");
        Assert.Single(r.Warnings);
        Assert.Contains("未知参数", r.Warnings[0]);
        Assert.False(r.Has("nonexistent"));
    }

    /// <summary>GUI 不支持的能力要说明原因，而不是静默丢弃。</summary>
    [Theory]
    [InlineData("--jobs 4", "jobs")]
    [InlineData("--delete-source-files", "delete-source-files")]
    [InlineData("--doit", "doit")]
    [InlineData("--info", "info")]
    [InlineData("--error-file txt", "error-file")]
    public void Parse_UnsupportedOption_WarnsWithReason(string input, string name)
    {
        var r = CliArgParser.Parse(input);
        Assert.Single(r.Warnings);
        Assert.Contains(name, r.Warnings[0]);
        Assert.Contains("不生效", r.Warnings[0]);
        Assert.False(r.Has(name));
    }

    [Fact]
    public void Parse_MissingValue_Warns()
    {
        var r = CliArgParser.Parse("--video-quality");
        Assert.Single(r.Warnings);
        Assert.Contains("缺少取值", r.Warnings[0]);
    }

    [Theory]
    [InlineData("--video-quality abc")]
    [InlineData("--dimension x")]
    public void Parse_NonNumericValue_Warns(string input)
    {
        var r = CliArgParser.Parse(input);
        Assert.Single(r.Warnings);
        Assert.Contains("需要数字", r.Warnings[0]);
    }

    /// <summary>码率格式：纯数字或带 k/m/g 单位。</summary>
    [Theory]
    [InlineData("--video-bitrate 3000000", true)]
    [InlineData("--video-bitrate 3M", true)]
    [InlineData("--video-bitrate 800k", true)]
    [InlineData("--video-bitrate 1.5g", true)]
    [InlineData("--video-bitrate abc", false)]
    [InlineData("--video-bitrate 3x", false)]
    [InlineData("--video-bitrate M3", false)]
    public void Parse_BitrateValidation(string input, bool valid)
    {
        var r = CliArgParser.Parse(input);
        Assert.Equal(valid, r.Warnings.Count == 0);
    }

    [Fact]
    public void Parse_InvalidChoice_Warns()
    {
        var r = CliArgParser.Parse("--decode-mode turbo");
        Assert.Single(r.Warnings);
        Assert.Contains("不合法", r.Warnings[0]);
        Assert.Contains("auto", r.Warnings[0]);
    }

    /// <summary>
    /// speed 的 0 是 mediac 文档化的「不变速」取值（也是 CLI 默认值），必须放行；
    /// 只拦真正越界的倍率。
    /// </summary>
    [Theory]
    [InlineData("0", true)]
    [InlineData("0.5", true)]
    [InlineData("1.5", true)]
    [InlineData("2.0", true)]
    [InlineData("0.4", false)]
    [InlineData("2.5", false)]
    public void Parse_SpeedRange(string value, bool valid)
    {
        var r = CliArgParser.Parse($"--speed {value}");
        Assert.Equal(valid, r.Warnings.Count == 0);
    }

    [Fact]
    public void Parse_NonOptionToken_Warns()
    {
        var r = CliArgParser.Parse("garbage --strict");
        Assert.Single(r.Warnings);
        Assert.Contains("无法识别", r.Warnings[0]);
        Assert.Equal("true", r.Get("strict"));   // 其余参数仍正常生效
    }

    [Fact]
    public void Parse_DuplicateOption_LastWinsWithWarning()
    {
        var r = CliArgParser.Parse("--video-quality 20 --video-quality 24");
        Assert.Equal("24", r.Get("video-quality"));
        Assert.Contains(r.Warnings, w => w.Contains("重复"));
    }

    [Fact]
    public void Parse_EmptyInput_NoWarnings()
    {
        foreach (var input in new[] { "", "   ", null })
        {
            var r = CliArgParser.Parse(input);
            Assert.Empty(r.Warnings);
            Assert.Empty(r.Values);
        }
    }

    /// <summary>解析不得因用户输入而抛异常。</summary>
    [Theory]
    [InlineData("--")]
    [InlineData("---")]
    [InlineData("=")]
    [InlineData("--=5")]
    [InlineData("--video-quality=")]
    public void Parse_MalformedInput_DoesNotThrow(string input)
    {
        var r = CliArgParser.Parse(input);
        Assert.NotNull(r);
    }

    // ------------------------------------------------------------------
    // 参数表本身
    // ------------------------------------------------------------------

    /// <summary>
    /// 参数名与别名必须唯一——但按 mediac 的语义是<b>区分大小写</b>的。
    ///
    /// mediac（yargs）默认大小写敏感，短别名刻意区分：
    /// <c>-o</c> 是 output、<c>-O</c> 是 override；<c>-e</c> 是 extensions、<c>-E</c> 是 exclude。
    /// 若按忽略大小写校验，会误报这些合法别名冲突；若实现按忽略大小写查表，
    /// 这些别名会互相覆盖、参数解析到错误的目标。
    /// </summary>
    [Fact]
    public void CliOptions_NamesAndAliasesAreUnique_CaseSensitive()
    {
        var seen = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var o in CliOptions.All)
        {
            Assert.False(seen.ContainsKey(o.Name),
                $"参数名重复: {o.Name}（已被 {seen.GetValueOrDefault(o.Name)} 占用）");
            seen[o.Name] = o.Name;

            foreach (var a in o.Aliases ?? [])
            {
                Assert.False(seen.ContainsKey(a),
                    $"别名冲突: {a}（{o.Name} 与 {seen.GetValueOrDefault(a)}）");
                seen[a] = o.Name;
            }
        }
    }

    /// <summary>
    /// 大小写敏感必须真的生效：-o 与 -O 解析到不同参数。
    /// 这条锁定上面那个坑——查表若忽略大小写，二者会撞成同一个。
    /// </summary>
    [Fact]
    public void Resolve_ShortAliasesAreCaseSensitive()
    {
        Assert.Equal("output", CliOptions.Resolve("o")?.Name);
        Assert.Equal("override", CliOptions.Resolve("O")?.Name);
        Assert.Equal("extensions", CliOptions.Resolve("e")?.Name);
        Assert.Equal("exclude", CliOptions.Resolve("E")?.Name);
    }

    [Fact]
    public void CliOptions_UnsupportedEntries_ExplainReason()
    {
        foreach (var o in CliOptions.All.Where(o => !o.Supported))
        {
            Assert.False(string.IsNullOrWhiteSpace(o.Reason),
                $"不支持的参数 {o.Name} 必须说明原因");
        }
    }

    [Fact]
    public void CliOptions_ChoiceEntries_DeclareChoices()
    {
        foreach (var o in CliOptions.All.Where(o => o.Kind == CliValueKind.Choice))
        {
            Assert.NotNull(o.Choices);
            Assert.NotEmpty(o.Choices!);
        }
    }

    [Fact]
    public void CliOptions_EveryEntryHasDescription()
    {
        foreach (var o in CliOptions.All)
        {
            Assert.False(string.IsNullOrWhiteSpace(o.Description), $"{o.Name} 缺少说明");
        }
    }
}
