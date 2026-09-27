using System;
using System.Drawing;
using System.Linq;
using System.Windows.Forms;
using Xunit;

namespace MediaCli.Transcode.Gui.Tests;

public class ParamsFlowFormTests
{
    private static TextBox Preview(ParamsFlowForm form) => Ui.Require<TextBox>(form, "paramPreviewBox");
    private static TextBox Trial(ParamsFlowForm form) => Ui.Require<TextBox>(form, "paramTrialBox");
    private static FlowLayoutPanel FlowPanel(ParamsFlowForm form) => Ui.Require<FlowLayoutPanel>(form, "paramFlowPanel");
    private static TextBox DescBox(ParamsFlowForm form) => Ui.Require<TextBox>(form, "paramDescBox");

    [Fact]
    public void Form_ContainsAllSupportedSettableParamsAsChips()
    {
        Ui.RunWithParamsFlow(form =>
        {
            var expectedCount = CliOptions.All.Count(o => o.Supported && o.ShadowedBy == null);
            var actualCount = FlowPanel(form).Controls.Cast<Control>().Count(c => c.Name.StartsWith("chip_", StringComparison.Ordinal));
            Assert.Equal(expectedCount, actualCount);

            // 描述框中也包含所有参数说明
            var desc = DescBox(form).Text;
            Assert.Contains("--video-bitrate", desc);
            Assert.Contains("--video-quality", desc);
            Assert.Contains("--video-copy", desc);
        });
    }

    [Theory]
    [InlineData("jobs")]
    [InlineData("doit")]
    [InlineData("delete-source-files")]
    [InlineData("show-presets")]
    [InlineData("preset")]
    [InlineData("output")]
    [InlineData("output-mode")]
    [InlineData("hwaccel")]
    [InlineData("decode-mode")]
    [InlineData("override")]
    [InlineData("strict")]
    [InlineData("debug")]
    [InlineData("anime")]
    public void NonSettableParams_AreNotListed(string name)
    {
        Ui.RunWithParamsFlow(form =>
        {
            Assert.Null(Ui.Find(form, "chip_" + name));
        });
    }

    [Fact]
    public void Chips_HaveNameInputAndDelimiter()
    {
        Ui.RunWithParamsFlow(form =>
        {
            var chip = Ui.Require<FlowLayoutPanel>(form, "chip_video-quality");
            Assert.Equal(3, chip.Controls.Count);
            Assert.Equal("lbl_video-quality", chip.Controls[0].Name);
            Assert.Equal("txt_video-quality", chip.Controls[1].Name);
            Assert.Equal("|", chip.Controls[2].Text);

            var flagChip = Ui.Require<FlowLayoutPanel>(form, "chip_video-copy");
            Assert.Equal(2, flagChip.Controls.Count);
            Assert.Equal("chk_video-copy", flagChip.Controls[0].Name);
            Assert.Equal("--video-copy", flagChip.Controls[0].Text);
            Assert.Equal("|", flagChip.Controls[1].Text);
        });
    }

    [Fact]
    public void Open_FillsFormFromExistingArgs()
    {
        Ui.RunWithParamsFlow(form =>
        {
            Assert.Equal("26", Ui.Require<TextBox>(form, "txt_video-quality").Text);
            Assert.Equal("--video-quality=26", Preview(form).Text);
        }, "hevc_2k", "--video-quality=26");
    }

    [Fact]
    public void Open_FillsCheckBoxForFlag()
    {
        Ui.RunWithParamsFlow(form =>
        {
            Assert.True(Ui.Require<CheckBox>(form, "chk_video-copy").Checked);
            Assert.Equal("--video-copy", Preview(form).Text);
        }, "hevc_2k", "--video-copy");
    }

    [Fact]
    public void Open_NormalizesAliasToCanonicalName()
    {
        Ui.RunWithParamsFlow(form =>
        {
            Assert.Equal("--override --video-bitrate=3M", Preview(form).Text);
        }, "hevc_2k", "-O --vb 3M");
    }

    [Fact]
    public void EditNumber_UpdatesPreviewAndTrial()
    {
        Ui.RunWithParamsFlow(form =>
        {
            var txt = Ui.Require<TextBox>(form, "txt_video-quality");
            txt.Text = "22";
            Assert.Equal("--video-quality=22", Preview(form).Text);

            Ui.Pump(300); // 等待 trialTimer 防抖 (250ms)
            var trial = Trial(form).Text;
            Assert.Contains("-cq 22", trial);
        });
    }

    [Fact]
    public void Flag_TogglesPreviewText()
    {
        Ui.RunWithParamsFlow(form =>
        {
            var chk = Ui.Require<CheckBox>(form, "chk_video-copy");
            chk.Checked = true;
            Assert.Contains("--video-copy", Preview(form).Text);

            chk.Checked = false;
            Assert.DoesNotContain("--video-copy", Preview(form).Text);
        });
    }

    [Fact]
    public void EmptyingInput_RemovesParam()
    {
        Ui.RunWithParamsFlow(form =>
        {
            var txt = Ui.Require<TextBox>(form, "txt_video-bitrate");
            txt.Text = "3M";
            Assert.Equal("--video-bitrate=3M", Preview(form).Text);

            txt.Text = "";
            Assert.Equal("", Preview(form).Text);
        });
    }

    [Fact]
    public void ClearAll_EmptiesAllInputsAndPreview()
    {
        Ui.RunWithParamsFlow(form =>
        {
            Ui.Require<TextBox>(form, "txt_video-bitrate").Text = "3M";
            Ui.Require<CheckBox>(form, "chk_video-copy").Checked = true;

            Ui.Click(form, "btnParamReset");

            Assert.Equal("", Preview(form).Text);
            Assert.Equal("", Ui.Require<TextBox>(form, "txt_video-bitrate").Text);
            Assert.False(Ui.Require<CheckBox>(form, "chk_video-copy").Checked);
        });
    }

    [Fact]
    public void PresetHeader_DisplaysDetailedParameters()
    {
        Ui.RunWithParamsFlow(form =>
        {
            var presetLbl = Ui.Require<Label>(form, "paramPresetLabel");
            Assert.Contains("hevc_2k", presetLbl.Text);
            Assert.Contains("hevc", presetLbl.Text);
            Assert.Contains("1920", presetLbl.Text);
        }, "hevc_2k");
    }

    [Fact]
    public void Validation_CatchesInvalidParameter()
    {
        Ui.RunWithParamsFlow(
            () => new TestableParamsFlowForm("hevc_2k", ""),
            form =>
            {
                var testable = (TestableParamsFlowForm)form;
                Ui.Require<TextBox>(form, "txt_fps").Text = "not_a_number";

                Ui.Click(form, "btnParamSave");

                Assert.NotEmpty(testable.ValidationErrors);
                Assert.Contains("fps", testable.ValidationErrors[0]);
                Assert.NotEqual(DialogResult.OK, form.DialogResult);
            });
    }

    [Fact]
    public void ManualEditMode_SyncsBackToControls()
    {
        Ui.RunWithParamsFlow(form =>
        {
            Ui.Click(form, "btnParamEditMode");
            var preview = Preview(form);
            Assert.False(preview.ReadOnly);

            preview.Text = "--video-bitrate=5M --video-copy";
            Ui.Click(form, "btnParamEditMode"); // 退出编辑模式，同步

            Assert.True(preview.ReadOnly);
            Assert.Equal("5M", Ui.Require<TextBox>(form, "txt_video-bitrate").Text);
            Assert.True(Ui.Require<CheckBox>(form, "chk_video-copy").Checked);
        });
    }
}
