using System;
using System.Collections;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Windows.Forms;
using Xunit;

namespace MediaCli.Transcode.Gui.Tests;

public class ParamsFormTests
{
	private static TextBox Preview(ParamsForm form)
	{
		return Ui.Require<TextBox>(form, "paramPreviewBox");
	}

	private static TextBox Trial(ParamsForm form)
	{
		return Ui.Require<TextBox>(form, "paramTrialBox");
	}

	private static Panel ScrollPanel(ParamsForm form)
	{
		return Ui.Require<Panel>(form, "paramScrollPanel");
	}

	[Fact]
	public void Form_HasNoGroupNavigation()
	{
		Ui.RunWithParams((ParamsForm form) =>
		{
			Assert.Null(Ui.Find(form, "paramGroupList"));
			Assert.Equal<int>(CliOptions.All.Count((CliOption o) => o.Supported && o.ShadowedBy == null), ScrollPanel(form).Controls.Cast<Control>().Count((Control c) => c.Name.StartsWith("row_", StringComparison.Ordinal)));
		});
	}

	[Theory]
	[InlineData(new object[] { "jobs" })]
	[InlineData(new object[] { "doit" })]
	[InlineData(new object[] { "delete-source-files" })]
	[InlineData(new object[] { "show-presets" })]
	[InlineData(new object[] { "preset" })]
	[InlineData(new object[] { "output" })]
	[InlineData(new object[] { "output-mode" })]
	[InlineData(new object[] { "hwaccel" })]
	[InlineData(new object[] { "decode-mode" })]
	[InlineData(new object[] { "override" })]
	[InlineData(new object[] { "strict" })]
	[InlineData(new object[] { "debug" })]
	[InlineData(new object[] { "anime" })]
	public void NonSettableParams_AreNotListed(string name)
	{
		Ui.RunWithParams((ParamsForm form) =>
		{
			Assert.Null(Ui.Find(form, "row_" + name));
		});
	}

	[Fact]
	public void Rows_DoNotOverlapAndChildrenStayInside()
	{
		Ui.RunWithParams((ParamsForm form) =>
		{
			List<Control> list = (from Control c in ScrollPanel(form).Controls
				where c.Name.StartsWith("row_", StringComparison.Ordinal)
				orderby c.Top
				select c).ToList();
			Assert.NotEmpty((IEnumerable)list);
			for (int num = 0; num < list.Count; num++)
			{
				Control control = list[num];
				foreach (Control control4 in control.Controls)
				{
					Assert.True(control4.Bottom <= control.Height, $"{control.Name} 的子控件 {control4.Name} 越出行边界: Bottom={control4.Bottom} > 行高={control.Height}");
					Assert.True(control4.Right <= control.Width, $"{control.Name} 的子控件 {control4.Name} 越出右边界: Right={control4.Right} > 行宽={control.Width}");
				}
				if (num > 0)
				{
					Control control3 = list[num - 1];
					Assert.True(control.Top >= control3.Bottom, $"{control.Name}(Top={control.Top}) 与 {control3.Name}(Bottom={control3.Bottom}) 重叠");
				}
			}
		});
	}

	[Fact]
	public void Rows_HaveNameDescriptionAndInput()
	{
		Ui.RunWithParams((ParamsForm form) =>
		{
			Panel panel = Ui.Require<Panel>(form, "row_video-quality");
			Assert.Equal<int>(3, panel.Controls.Count);
			Assert.Equal("lbl_video-quality", panel.Controls[0].Name);
			Assert.Equal("desc_video-quality", panel.Controls[1].Name);
			Assert.Equal("txt_video-quality", panel.Controls[2].Name);
		});
	}

	[Fact]
	public void Rows_ReflowOnResize()
	{
		Ui.RunWithParams((ParamsForm form) =>
		{
			int width = Ui.Require<Panel>(form, "row_video-quality").Width;
			form.Width += 200;
			form.PerformLayout();
			Ui.Pump(200);
			int width2 = Ui.Require<Panel>(form, "row_video-quality").Width;
			Assert.True(width2 > width, $"行宽未随窗口变化: {width} → {width2}");
			Panel panel = Ui.Require<Panel>(form, "row_video-quality");
			Control control = panel.Controls[2];
			Assert.True(control.Right <= panel.Width, "输入框越出行右边界");
		});
	}

	[Fact]
	public void List_IsScrolledToTop()
	{
		Ui.RunWithParams((ParamsForm form) =>
		{
			Panel panel = ScrollPanel(form);
			Assert.Equal<int>(0, panel.VerticalScroll.Value);
			Control control = (from Control c in panel.Controls
				where c.Name.StartsWith("row_", StringComparison.Ordinal)
				orderby c.Top
				select c).First();
			Assert.True(control.Top >= 0, $"首行被滚出可视区: Top={control.Top}");
		});
	}

	[Fact]
	public void HeaderAndBody_DoNotOverlap()
	{
		Ui.RunWithParams((ParamsForm form) =>
		{
			Panel panel = Ui.Require<Panel>(form, "headerPanel");
			Panel panel2 = ScrollPanel(form);
			Assert.True(panel2.Top >= panel.Bottom, $"列表容器(Top={panel2.Top}) 与 header(Bottom={panel.Bottom}) 重叠");
		});
	}

	[Fact]
	public void FooterControls_AreFullyVisibleAtMinimumSize()
	{
		Ui.RunWithParams((ParamsForm form) =>
		{
			form.Size = form.MinimumSize;
			form.PerformLayout();
			Ui.Pump(300);
			Size clientSize = form.ClientSize;
			List<string> list = new List<string>();
			string[] array = new string[7] { "btnParamSave", "btnParamCancel", "btnParamReset", "btnParamCopy", "btnParamEditMode", "paramTrialBox", "paramPreviewBox" };
			foreach (string text in array)
			{
				Control? control = Ui.Find(form, text);
				Assert.NotNull(control);
				int y = form.PointToClient(control.PointToScreen(new Point(0, control.Height))).Y;
				if (y > clientSize.Height)
				{
					list.Add($"{text}: 底部 {y} > 客户区高 {clientSize.Height}");
				}
			}
			Assert.True(list.Count == 0, "最小尺寸下底部控件不可见:" + Environment.NewLine + string.Join(Environment.NewLine, list));
		});
	}

	[Fact]
	public void Open_FillsFormFromExistingArgs()
	{
		Ui.RunWithParams((ParamsForm form) =>
		{
			Assert.Equal("26", Ui.Require<TextBox>(form, "txt_video-quality").Text);
			Assert.Equal("--video-quality=26", Preview(form).Text);
		}, "hevc_2k", "--video-quality=26");
	}

	[Fact]
	public void Open_FillsCheckBoxForFlag()
	{
		Ui.RunWithParams((ParamsForm form) =>
		{
			Assert.True(Ui.Require<CheckBox>(form, "chk_video-copy").Checked);
			Assert.Equal("--video-copy", Preview(form).Text);
		}, "hevc_2k", "--video-copy");
	}

	[Fact]
	public void Open_NormalizesAliasToCanonicalName()
	{
		Ui.RunWithParams((ParamsForm form) =>
		{
			Assert.Equal("--override --video-bitrate=3M", Preview(form).Text);
		}, "hevc_2k", "-O --vb 3M");
	}

	[Fact]
	public void EditNumber_UpdatesPreviewText()
	{
		Ui.RunWithParams((ParamsForm form) =>
		{
			Ui.Require<TextBox>(form, "txt_video-quality").Text = "26";
			Assert.Equal("--video-quality=26", Preview(form).Text);
		});
	}

	[Fact]
	public void ClearNumber_RemovesParamFromPreviewText()
	{
		Ui.RunWithParams((ParamsForm form) =>
		{
			Ui.Require<TextBox>(form, "txt_video-quality").Text = "26";
			Ui.Require<TextBox>(form, "txt_video-quality").Text = "";
			Assert.Equal("", Preview(form).Text);
		});
	}

	[Fact]
	public void ToggleFlag_AddsAndRemovesParam()
	{
		Ui.RunWithParams((ParamsForm form) =>
		{
			CheckBox checkBox = Ui.Require<CheckBox>(form, "chk_audio-copy");
			checkBox.Checked = true;
			Assert.Equal("--audio-copy", Preview(form).Text);
			checkBox.Checked = false;
			Assert.Equal("", Preview(form).Text);
		});
	}

	[Fact]
	public void Edit_ValueWithSpace_IsQuotedInPreviewText()
	{
		Ui.RunWithParams((ParamsForm form) =>
		{
			Ui.Require<TextBox>(form, "txt_metadata").Text = "title=My Video";
			Assert.Equal("--metadata=\"title=My Video\"", Preview(form).Text);
		});
	}

	[Fact]
	public void MainControlledParams_AreAbsent()
	{
		Ui.RunWithParams((ParamsForm form) =>
		{
			string[] array = new string[4] { "preset", "output", "hwaccel", "override" };
			foreach (string text in array)
			{
				Assert.Null(Ui.Find(form, "row_" + text));
				Assert.Null(Ui.Find(form, "txt_" + text));
				Assert.Null(Ui.Find(form, "cmb_" + text));
				Assert.Null(Ui.Find(form, "chk_" + text));
			}
		});
	}

	[Fact]
	public void MainControlledArg_FromText_IsPreservedInPreview()
	{
		Ui.RunWithParams((ParamsForm form) =>
		{
			Assert.Equal("--preset=av1_2k --video-quality=26", Preview(form).Text);
			Assert.Equal("26", Ui.Require<TextBox>(form, "txt_video-quality").Text);
		}, "hevc_2k", "--preset av1_2k --video-quality=26");
	}

	[Fact]
	public void Buttons_HaveVisibleTextAndDoNotOverlapStatusLine()
	{
		Ui.RunWithParams((ParamsForm form) =>
		{
			Label label = Ui.Require<Label>(form, "paramStatusLabel");
			string[] array = new string[5] { "btnParamSave", "btnParamCancel", "btnParamReset", "btnParamCopy", "btnParamEditMode" };
			foreach (string text in array)
			{
				Button button = Ui.Require<Button>(form, text);
				Assert.False(string.IsNullOrWhiteSpace(button.Text), text + " 没有文字");
				Assert.True(button.Right <= form.ClientSize.Width, $"{text} 跑出窗口右边界: Right={button.Right} > {form.ClientSize.Width}");
				Assert.False(button.Bounds.IntersectsWith(label.Bounds), $"{text} 与状态行重叠: {button.Bounds} vs {label.Bounds}");
			}
		});
	}

	[Fact]
	public void Search_FiltersParamsByKeyword()
	{
		Ui.RunWithParams((ParamsForm form) =>
		{
			Ui.Require<TextBox>(form, "paramSearchBox").Text = "bitrate";
			Ui.Pump(100);
			Assert.NotNull(Ui.Find(form, "row_video-bitrate"));
			Assert.NotNull(Ui.Find(form, "row_audio-bitrate"));
			Assert.Null(Ui.Find(form, "row_video-quality"));
		});
	}

	[Fact]
	public void ValidateAll_CleanParams_ReturnsNoError()
	{
		Ui.RunWithParams((ParamsForm form) =>
		{
			Ui.Require<TextBox>(form, "txt_video-quality").Text = "26";
			Assert.Empty((IEnumerable)form.ValidateAll());
		});
	}

	[Theory]
	[InlineData(new object[] { "--speed 99", "speed" })]
	[InlineData(new object[] { "--video-quality abc", "video-quality" })]
	[InlineData(new object[] { "--jobs 4", "jobs" })]
	[InlineData(new object[] { "--no-such-param 1", "no-such-param" })]
	public void ValidateAll_RejectsInvalidOrUnsupported(string cliArgs, string expectMention)
	{
		Ui.RunWithParams((ParamsForm form) =>
		{
			IReadOnlyList<string> readOnlyList = form.ValidateAll();
			Assert.NotEmpty((IEnumerable)readOnlyList);
			Assert.Contains<string>((IEnumerable<string>)readOnlyList, (Predicate<string>)((string e) => e.Contains(expectMention, StringComparison.OrdinalIgnoreCase)));
		}, "hevc_2k", cliArgs);
	}

	[Fact]
	public void ValidateAll_RejectsFfargsBitrateWithUnit()
	{
		Ui.RunWithParams((ParamsForm form) =>
		{
			Ui.Require<TextBox>(form, "txt_ffargs").Text = "vb=3M";
			Assert.NotEmpty((IEnumerable)form.ValidateAll());
		});
	}

	[Fact]
	public void Open_WithInvalidArgs_KeepsRawTextAndBlocksSave()
	{
		TestableParamsForm? probe = null;
		Ui.RunWithParams(() => probe = new TestableParamsForm("hevc_2k", "--speed 99"), (ParamsForm form) =>
		{
			Assert.Equal("--speed 99", Preview(form).Text);
			Assert.NotEmpty((IEnumerable)form.ValidateAll());
			Ui.Click(form, "btnParamSave");
			Ui.Pump(100);
			Assert.NotNull(probe);
			Assert.NotEmpty((IEnumerable)probe.ValidationErrors);
			Assert.NotEqual<DialogResult>(DialogResult.OK, form.DialogResult);
		});
	}

	[Fact]
	public void Save_WithInvalidArgs_IsBlockedAndKeepsDialogOpen()
	{
		TestableParamsForm? probe = null;
		Ui.RunWithParams(() => probe = new TestableParamsForm("hevc_2k", ""), (ParamsForm form) =>
		{
			Ui.Click(form, "btnParamEditMode");
			Preview(form).Text = "--speed 99";
			Ui.Click(form, "btnParamEditMode");
			Ui.Pump(100);
			Ui.Click(form, "btnParamSave");
			Ui.Pump(100);
			Assert.NotNull(probe);
			Assert.NotEmpty((IEnumerable)probe.ValidationErrors);
			Assert.NotEqual<DialogResult>(DialogResult.OK, form.DialogResult);
		});
	}

	[Fact]
	public void Save_WithCleanArgs_WritesBackNormalizedText()
	{
		Ui.RunWithParams((ParamsForm form) =>
		{
			Ui.Require<TextBox>(form, "txt_video-quality").Text = "26";
			Ui.Click(form, "btnParamSave");
			Assert.Equal("--video-quality=26", form.CliArgs);
			DeserializeResult deserializeResult = ParamSerializer.Deserialize(form.CliArgs);
			Assert.Empty((IEnumerable)deserializeResult.Warnings);
			Assert.Equal("26", deserializeResult.Values["video-quality"]);
		});
	}

	[Fact]
	public void ManualEdit_PastedArgsRefillTheForm()
	{
		Ui.RunWithParams((ParamsForm form) =>
		{
			Ui.Click(form, "btnParamEditMode");
			Ui.Pump(100);
			Assert.False(Preview(form).ReadOnly);
			Preview(form).Text = "--audio-bitrate=320k --video-copy";
			Ui.Click(form, "btnParamEditMode");
			Ui.Pump(100);
			Assert.True(Preview(form).ReadOnly);
			Assert.Equal("--audio-bitrate=320k --video-copy", Preview(form).Text);
			Assert.Equal("320k", Ui.Require<TextBox>(form, "txt_audio-bitrate").Text);
			Assert.True(Ui.Require<CheckBox>(form, "chk_video-copy").Checked);
		});
	}

	[Fact]
	public void Reset_ClearsAllValues()
	{
		Ui.RunWithParams((ParamsForm form) =>
		{
			Ui.Require<TextBox>(form, "txt_video-quality").Text = "26";
			Ui.Click(form, "btnParamReset");
			Ui.Pump(100);
			Assert.Equal("", Preview(form).Text);
			Assert.Equal("", Ui.Require<TextBox>(form, "txt_video-quality").Text);
		}, "hevc_2k", "--video-quality=26");
	}

	[Fact]
	public void Trial_ShowsSingleLineCommandWithOverride()
	{
		Ui.RunWithParams((ParamsForm form) =>
		{
			Ui.Require<TextBox>(form, "txt_video-quality").Text = "26";
			Ui.Pump(500);
			string text = Trial(form).Text;
			Assert.DoesNotContain(Environment.NewLine, text);
			Assert.StartsWith("ffmpeg ", text);
			Assert.Contains("-cq 26", text);
			Assert.Contains("-i <输入>", text);
		});
	}

	[Fact]
	public void Trial_DoesNotClaimConcreteEncoder()
	{
		Ui.RunWithParams((ParamsForm form) =>
		{
			string text = Trial(form).Text;
			Assert.Contains("-c:v hevc", text);
			Assert.DoesNotContain("nvenc", text);
		});
	}

	[Fact]
	public void Trial_WithNoOverrides_StillShowsPresetValues()
	{
		Ui.RunWithParams((ParamsForm form) =>
		{
			string text = Trial(form).Text;
			Assert.Contains("-cq 23", text);
			Assert.Contains("-b:a 192k", text);
		});
	}
}
