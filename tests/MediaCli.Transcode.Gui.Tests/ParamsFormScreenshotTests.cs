using System;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Windows.Forms;
using Xunit;

namespace MediaCli.Transcode.Gui.Tests;

public class ParamsFormScreenshotTests
{
	private const int MinDistinctColors = 40;

	private static void CaptureAndVerify(Form form, string name)
	{
		string text = Ui.CaptureWindow(form, name);
		Assert.True(File.Exists(text), "截图未生成: " + text);
		Assert.True(new FileInfo(text).Length > 5000, "PNG 体积过小，可能未渲染");
		using Bitmap bmp = new Bitmap(text);
		int num = Ui.CountDistinctColors(bmp);
		Assert.True(num >= 40, $"截图疑似空白（仅 {num} 种颜色）: {text}");
	}

	[Fact]
	public void Screenshot_ParamsFlowDefault()
	{
		Ui.RunWithParamsFlow(form =>
		{
			CaptureAndVerify(form, "08-params-flow-compact");
		});
	}

	[Fact]
	public void Screenshot_ParamsDefault()
	{
		Ui.RunWithParams((ParamsForm form) =>
		{
			CaptureAndVerify(form, "06-params-default");
			Assert.Equal<int>(CliOptions.All.Count((CliOption o) => o.Supported && o.ShadowedBy == null), Ui.AllControls(form).Count((Control c) => c.Name.StartsWith("row_", StringComparison.Ordinal)));
		});
	}

	[Fact]
	public void Screenshot_ParamsWithOverrides()
	{
		Ui.RunWithParams((ParamsForm form) =>
		{
			Ui.Require<TextBox>(form, "txt_video-quality").Text = "26";
			Ui.Require<TextBox>(form, "txt_metadata").Text = "title=Demo";
			Ui.Require<CheckBox>(form, "chk_video-copy").Checked = true;
			Ui.Pump(600);
			CaptureAndVerify(form, "07-params-filled");
			string text = Ui.Require<TextBox>(form, "paramTrialBox").Text;
			Assert.StartsWith("ffmpeg ", text);
			Assert.Contains("-c:v copy", text);
			Assert.DoesNotContain("-cq", text);
			Assert.Contains("-metadata title=Demo", text);
		});
	}
}
