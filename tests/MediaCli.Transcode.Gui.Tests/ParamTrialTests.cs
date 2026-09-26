using System.Collections;
using System.Linq;
using Xunit;

namespace MediaCli.Transcode.Gui.Tests;

public class ParamTrialTests
{
	private static ParamTrialRow Row(ParamTrialResult r, string label)
	{
		return r.Rows.First((ParamTrialRow x) => x.Label == label);
	}

	[Fact]
	public void Run_NoArgs_ReportsPresetDefaults()
	{
		ParamTrialResult paramTrialResult = ParamTrial.Run("hevc_2k", "");
		Assert.Null(paramTrialResult.Error);
		Assert.Equal("hevc_2k", paramTrialResult.PresetName);
		Assert.Equal("video", paramTrialResult.PresetType);
		Assert.Equal(".mp4", paramTrialResult.Container);
		Assert.Equal("1920", Row(paramTrialResult, "目标长边").Value);
		Assert.Null(Row(paramTrialResult, "目标长边").Note);
		Assert.Equal("23", Row(paramTrialResult, "视频质量").Value);
		Assert.Equal("8M", Row(paramTrialResult, "峰值码率").Value);
		Assert.Equal("192k", Row(paramTrialResult, "音频码率").Value);
		Assert.Equal("aac", Row(paramTrialResult, "音频编码").Value);
		Assert.Equal("[SHANA] ", Row(paramTrialResult, "文件名前缀").Value);
		Assert.Equal("_{preset}", Row(paramTrialResult, "文件名后缀").Value);
	}

	[Fact]
	public void Run_NoArgs_MaxBitrateIsMarkedAsNotOverridable()
	{
		ParamTrialResult r = ParamTrial.Run("hevc_2k", "--video-quality 26");
		Assert.Equal("8M", Row(r, "峰值码率").Value);
		Assert.Equal("预设内置，无覆盖通道", Row(r, "峰值码率").Note);
	}

	[Fact]
	public void Run_NoArgs_VideoCodecIsFamilyNotConcreteEncoder()
	{
		ParamTrialResult r = ParamTrial.Run("hevc_2k", "");
		string value = Row(r, "视频编码").Value;
		Assert.Contains("hevc", value);
		Assert.Contains("硬件层", value);
		Assert.DoesNotContain("nvenc", value);
	}

	[Theory]
	[InlineData(new object[] { "--video-quality 26", "视频质量", "26", "23" })]
	[InlineData(new object[] { "--audio-bitrate 320k", "音频码率", "320k", "192k" })]
	[InlineData(new object[] { "--dimension 1280", "目标长边", "1280", "1920" })]
	[InlineData(new object[] { "--fps 30", "帧率", "30", "-" })]
	[InlineData(new object[] { "--speed 1.5", "变速", "1.5", "1" })]
	public void Run_Override_UpdatesValueAndMarksOverridden(string cliArgs, string label, string expected, string defaultText)
	{
		ParamTrialResult paramTrialResult = ParamTrial.Run("hevc_2k", cliArgs);
		Assert.Null(paramTrialResult.Error);
		Assert.Equal(expected, Row(paramTrialResult, label).Value);
		Assert.Equal(defaultText, Row(paramTrialResult, label).DefaultValue);
		Assert.Equal("已覆盖", Row(paramTrialResult, label).Note);
	}

	[Fact]
	public void Run_VideoCodec_ExplicitEncoderIsReportedAsIs()
	{
		ParamTrialResult r = ParamTrial.Run("hevc_2k", "--video-codec libx265");
		Assert.Equal("libx265", Row(r, "视频编码").Value);
		Assert.Equal("已覆盖", Row(r, "视频编码").Note);
	}

	[Fact]
	public void Run_VideoCopy_SetsCopyAndMarksQualityIgnored()
	{
		ParamTrialResult r = ParamTrial.Run("hevc_2k", "--video-copy");
		Assert.Equal("copy（流复制）", Row(r, "视频编码").Value);
		Assert.Contains("已忽略", Row(r, "视频质量").Note ?? "");
		Assert.Contains("已忽略", Row(r, "视频码率").Note ?? "");
	}

	[Fact]
	public void Run_VideoCopyWithQuality_ReportsQualityIgnoredNotApplied()
	{
		ParamTrialResult r = ParamTrial.Run("hevc_2k", "--video-copy --video-quality 26");
		ParamTrialRow paramTrialRow = Row(r, "视频质量");
		Assert.Equal("23", paramTrialRow.Value);
		Assert.Equal("23", paramTrialRow.DefaultValue);
		Assert.Contains("已忽略", paramTrialRow.Note ?? "");
		Assert.DoesNotContain("已覆盖", paramTrialRow.Note ?? "");
	}

	[Fact]
	public void Run_Metadata_IsParsedIntoPairs()
	{
		ParamTrialResult r = ParamTrial.Run("hevc_2k", "--metadata=\"title=My Video\"");
		Assert.Equal("title=My Video", Row(r, "元数据").Value);
		Assert.Equal("已覆盖", Row(r, "元数据").Note);
	}

	[Fact]
	public void Run_Prefix_OverridesInheritedValue()
	{
		ParamTrialResult r = ParamTrial.Run("hevc_2k", "--suffix=_x");
		Assert.Equal("_x", Row(r, "文件名后缀").Value);
		Assert.Equal("_{preset}", Row(r, "文件名后缀").DefaultValue);
	}

	[Fact]
	public void Run_PresetAlias_IsNormalizedLikeCore()
	{
		ParamTrialResult paramTrialResult = ParamTrial.Run("hevc", "");
		Assert.Equal("hevc_2k", paramTrialResult.PresetName);
	}

	[Fact]
	public void Run_AnimeAlias_SetsAnimeFlag()
	{
		ParamTrialResult paramTrialResult = ParamTrial.Run("hevc_anime", "");
		Assert.Equal("hevc_2k", paramTrialResult.PresetName);
		Assert.Equal("开", Row(paramTrialResult, "动漫调优").Value);
	}

	[Fact]
	public void Run_Ffargs_AppliesThroughWhitelist()
	{
		ParamTrialResult r = ParamTrial.Run("hevc_2k", "--ffargs=vb=3000000");
		Assert.Equal("3M", Row(r, "视频码率").Value);
		Assert.Equal("已覆盖", Row(r, "视频码率").Note);
	}

	[Fact]
	public void Run_UnknownParam_ProducesWarning()
	{
		ParamTrialResult paramTrialResult = ParamTrial.Run("hevc_2k", "--no-such-param 1");
		Assert.Null(paramTrialResult.Error);
		Assert.NotEmpty((IEnumerable)paramTrialResult.Warnings);
	}

	[Fact]
	public void Run_FfargsBitrateWithUnit_ProducesWarning()
	{
		ParamTrialResult paramTrialResult = ParamTrial.Run("hevc_2k", "--ffargs=vb=3M");
		Assert.NotEmpty((IEnumerable)paramTrialResult.Warnings);
	}

	[Fact]
	public void Run_UnknownPreset_ReturnsErrorInsteadOfThrowing()
	{
		ParamTrialResult paramTrialResult = ParamTrial.Run("no_such_preset", "");
		Assert.NotNull(paramTrialResult.Error);
		Assert.Empty((IEnumerable)paramTrialResult.Rows);
	}

	[Fact]
	public void Run_GarbageArgs_DoesNotThrow()
	{
		ParamTrialResult paramTrialResult = ParamTrial.Run("hevc_2k", "--video-quality abc --speed 99 --dimension -1");
		Assert.Null(paramTrialResult.Error);
		Assert.NotEmpty((IEnumerable)paramTrialResult.Warnings);
	}
}
