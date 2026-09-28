using System;
using System.Collections.Generic;
using System.Linq;
using MediaCli.Transcode.Model;
using MediaCli.Transcode.Presets;

namespace MediaCli.Transcode.Gui;

public sealed record ParamTrialRow(string Label, string Value, string DefaultValue, string? Note);
public sealed record ParamTrialResult(string PresetName, string? PresetType, string? Container, IReadOnlyList<ParamTrialRow> Rows, IReadOnlyList<string> Warnings, string? Error, string CommandLine);

public static class ParamTrial
{
	public static ParamTrialResult Run(string presetName, string cliArgs)
	{
		try
		{
			TranscodeSession.EnsurePresetsLoaded();
			GuiOptions guiOptions = new GuiOptions
			{
				Preset = presetName,
				CliArgs = cliArgs
			};
			FFmpegPreset fFmpegPreset = FFmpegPresets.CreateFromArgv(guiOptions.ToArgvShim());
			PresetUserArgs userArgs = fFmpegPreset.UserArgs;
			FFmpegPreset? preset = FFmpegPresets.GetPreset(fFmpegPreset.Name);
			List<ParamTrialRow> rows = new List<ParamTrialRow>
			{
				RowNum("目标长边", (userArgs.Dimension > 0) ? userArgs.Dimension : fFmpegPreset.Dimension, preset?.Dimension ?? 0, DimensionText),
				RowNum("视频质量", (userArgs.VideoQuality > 0) ? ((double)userArgs.VideoQuality) : fFmpegPreset.VideoQuality, preset?.VideoQuality ?? 0.0, NumText, userArgs.VideoCopy ? "已忽略（流复制不重新编码）" : null),
				RowNum("视频码率", (userArgs.VideoBitrate > 0) ? userArgs.VideoBitrate : fFmpegPreset.VideoBitrate, preset?.VideoBitrate ?? 0, BitrateText, userArgs.VideoCopy ? "已忽略（流复制不重新编码）" : null),
				RowNum("峰值码率", fFmpegPreset.MaxBitrate, preset?.MaxBitrate ?? 0, BitrateText, "预设内置，无覆盖通道"),
				RowText("视频编码", VideoCodecText(fFmpegPreset, userArgs), preset?.VideoCodecFamily),
				RowText("音频编码", userArgs.AudioCodec ?? fFmpegPreset.AudioCodec, preset?.AudioCodec),
				RowNum("音频码率", (userArgs.AudioBitrate > 0) ? userArgs.AudioBitrate : fFmpegPreset.AudioBitrate, preset?.AudioBitrate ?? 0, BitrateText, userArgs.AudioCopy ? "已忽略（流复制不重新编码）" : null),
				RowNum("音频质量", (userArgs.AudioQuality > 0) ? ((double)userArgs.AudioQuality) : fFmpegPreset.AudioQuality, preset?.AudioQuality ?? 0.0, NumText),
				RowNum("帧率", (userArgs.Framerate > 0.0) ? userArgs.Framerate : fFmpegPreset.Framerate, preset?.Framerate ?? 0.0, NumText),
				RowNum("变速", Speed((userArgs.Speed > 0.0) ? userArgs.Speed : fFmpegPreset.Speed), Speed(preset?.Speed ?? 1.0), NumText),
				RowText("文件名前缀", fFmpegPreset.Prefix, preset?.Prefix),
				RowText("文件名后缀", fFmpegPreset.Suffix, preset?.Suffix),
				RowText("元数据", MetadataText(userArgs.MetadataPairs), null, (userArgs.MetadataPairs.Count > 0) ? "已覆盖" : null),
				RowText("动漫调优", userArgs.Anime ? "开" : "关", (preset == null) ? null : "关", userArgs.Anime ? "已覆盖" : null)
			};
			return new ParamTrialResult(fFmpegPreset.Name, fFmpegPreset.Type, fFmpegPreset.Format, rows, guiOptions.AllWarnings(), null, BuildCommandLine(fFmpegPreset, userArgs));
		}
		catch (Exception ex)
		{
			return new ParamTrialResult(presetName, null, null, Array.Empty<ParamTrialRow>(), Array.Empty<string>(), ex.Message, "");
		}
	}

	private static string BuildCommandLine(FFmpegPreset preset, PresetUserArgs ua)
	{
		List<string> list = new List<string> { "ffmpeg", "-i", "<输入>" };
		bool videoCopy = ua.VideoCopy;
		list.Add("-c:v");
		list.Add(videoCopy ? "copy" : (ua.VideoCodec ?? preset.VideoCodecFamily ?? "copy"));
		if (!videoCopy)
		{
			double num = ((ua.VideoQuality > 0) ? ((double)ua.VideoQuality) : preset.VideoQuality);
			if (num > 0.0)
			{
				list.Add(QualityFlag(preset.VideoCodecFamily));
				list.Add(num.ToString("0.##"));
			}
			long num2 = ((ua.VideoBitrate > 0) ? ua.VideoBitrate : preset.VideoBitrate);
			if (num2 > 0)
			{
				AddBitrate(list, "-b:v", num2);
			}
			if (preset.MaxBitrate > 0)
			{
				AddBitrate(list, "-maxrate", preset.MaxBitrate);
			}
			long num3 = ((ua.Dimension > 0) ? ua.Dimension : preset.Dimension);
			if (num3 > 0)
			{
				list.Add("-vf");
				list.Add($"scale=-2:{num3:0}");
			}
			double num4 = ((ua.Framerate > 0.0) ? ua.Framerate : preset.Framerate);
			if (num4 > 0.0)
			{
				list.Add("-r");
				list.Add(num4.ToString("0.###"));
			}
		}
		if (ua.AudioCopy)
		{
			list.Add("-c:a");
			list.Add("copy");
		}
		else
		{
			list.Add("-c:a");
			list.Add(ua.AudioCodec ?? preset.AudioCodec ?? "aac");
			long num5 = ((ua.AudioBitrate > 0) ? ua.AudioBitrate : preset.AudioBitrate);
			if (num5 > 0)
			{
				AddBitrate(list, "-b:a", num5);
			}
		}
		foreach (KeyValuePair<string, string> metadataPair in ua.MetadataPairs)
		{
			list.Add("-metadata");
			list.Add(metadataPair.Key + "=" + metadataPair.Value);
		}
		list.Add("<输出>" + (preset.Format ?? ".mp4"));
		return string.Join(" ", list);
	}

	private static void AddBitrate(List<string> parts, string flag, long bps)
	{
		parts.Add(flag);
		parts.Add(BitrateText(bps));
	}

	private static string QualityFlag(string? family)
	{
		// 说明：此处生成的是"示意命令"（界面与快照均标注"非最终命令"）。
		// 真实命令的质量参数由硬件层决定：CPU/hevc 用 -crf，NVENC 用 -cq，
		// QSV 用 -global_quality，AMF 用 -qvbr_quality_level（见 HwAccel.BuildEncoderArgs）。
		// 因此这里只按"软编"口径给出示意值，避免暗示 hevc 会用 -cq。
		if (!(family == "h264"))
		{
			if (family == "vp9")
			{
				return "-crf";
			}
			return "-crf";
		}
		return "-crf";
	}

	private static string? VideoCodecText(FFmpegPreset preset, PresetUserArgs ua)
	{
		if (ua.VideoCopy)
		{
			return "copy（流复制）";
		}
		if (!string.IsNullOrEmpty(ua.VideoCodec))
		{
			return ua.VideoCodec;
		}
		string? videoCodecFamily = preset.VideoCodecFamily;
		if (!string.IsNullOrEmpty(videoCodecFamily))
		{
			return videoCodecFamily + "（具体编码器由硬件层决定）";
		}
		return null;
	}

	private static double Speed(double v)
	{
		if (!(v > 0.0))
		{
			return 1.0;
		}
		return v;
	}

	private static string MetadataText(IReadOnlyList<KeyValuePair<string, string>> pairs)
	{
		if (pairs.Count != 0)
		{
			return string.Join("; ", pairs.Select((KeyValuePair<string, string> p) => p.Key + "=" + p.Value));
		}
		return "-";
	}

	private static ParamTrialRow RowNum(string label, double final, double def, Func<double, string> format, string? note = null)
	{
		return new ParamTrialRow(label, format(final), format(def), (Math.Abs(final - def) > 1E-09) ? "已覆盖" : note);
	}

	private static ParamTrialRow RowText(string label, string? final, string? def, string? note = null)
	{
		string text = (string.IsNullOrEmpty(final) ? "-" : final);
		string text2 = (string.IsNullOrEmpty(def) ? "-" : def);
		return new ParamTrialRow(label, text, text2, (!string.Equals(text, text2, StringComparison.Ordinal)) ? "已覆盖" : note);
	}

	private static string DimensionText(double v)
	{
		if (!(v > 0.0))
		{
			return "保持源尺寸";
		}
		return v.ToString("0");
	}

	private static string NumText(double v)
	{
		if (!(v > 0.0))
		{
			return "-";
		}
		return v.ToString("0.##");
	}

	private static string BitrateText(double bps)
	{
		if (bps <= 0.0)
		{
			return "-";
		}
		if (bps >= 1000000.0)
		{
			return (bps / 1000000.0).ToString("0.##") + "M";
		}
		if (bps >= 1000.0)
		{
			return (bps / 1000.0).ToString("0.##") + "k";
		}
		return bps.ToString("0") + "bps";
	}
}
