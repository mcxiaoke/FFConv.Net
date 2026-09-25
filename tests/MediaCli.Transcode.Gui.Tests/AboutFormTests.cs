using MediaCli.Transcode.Gui;
using Xunit;

namespace MediaCli.Transcode.Gui.Tests;

/// <summary>
/// 「使用说明」窗口的测试（需求 5）。
///
/// 这个窗口承载了从界面上移出的参数说明，因此必须确认它真的能打开、
/// 三个页签都有内容，而不是一个空白框。
/// </summary>
public class AboutFormTests
{
    /// <summary>AboutForm 与 MainForm 同为纯代码布局，DPI 基准同样不能漏。</summary>
    [Fact]
    public void AboutForm_DeclaresDpiScalingBasis()
    {
        Ui.RunInSta(() =>
        {
            using var form = new AboutForm();
            Assert.Equal(AutoScaleMode.Dpi, form.AutoScaleMode);
            Assert.NotEqual(0F, form.AutoScaleDimensions.Width);
        });
    }

    [Fact]
    public void AboutForm_HasThreeTabs()
    {
        Ui.RunWithAbout(form =>
        {
            var tabs = Ui.Require<TabControl>(form, "aboutTabs");
            Assert.Equal(3, tabs.TabPages.Count);

            var titles = tabs.TabPages.Cast<TabPage>().Select(p => p.Text).ToList();
            Assert.Equal("命令行参数", tabs.TabPages[0].Text);
            Assert.Contains("预设", titles);
            Assert.Contains("命令行参数", titles);
            Assert.Contains("使用提示", titles);
        });
    }

    /// <summary>预设页签必须列出全部可用预设，且不含 _base_* 继承基类。</summary>
    [Fact]
    public void AboutForm_PresetTab_ListsPresets()
    {
        Ui.RunWithAbout(form =>
        {
            var list = Ui.Require<ListView>(form, "presetList");
            Assert.NotEmpty(list.Items);

            var names = list.Items.Cast<ListViewItem>().Select(i => i.Text).ToList();
            Assert.Contains("hevc_2k", names);
            Assert.DoesNotContain(names, n => n.StartsWith('_'));

            // 选中首项时详情应有内容（联动生效）
            var detail = Ui.Require<RichTextBox>(form, "presetDetail");
            Assert.False(string.IsNullOrWhiteSpace(detail.Text), "预设详情为空");
            Assert.Contains("预设名：", detail.Text);
        });
    }

    /// <summary>命令行参数页签必须覆盖参数表里的每一项（含不支持的）。</summary>
    [Fact]
    public void AboutForm_CliTab_CoversAllOptions()
    {
        Ui.RunWithAbout(form =>
        {
            var list = Ui.Require<ListView>(form, "cliOptionList");
            var text = string.Join("\n", list.Items.Cast<ListViewItem>().Select(i => i.Text));

            foreach (var opt in CliOptions.All)
            {
                Assert.Contains($"--{opt.Name}", text);
            }

            // 不支持的项要标出来，而不是混在可用项里
            var statuses = list.Items.Cast<ListViewItem>()
                .Select(i => i.SubItems[3].Text).Distinct().ToList();
            Assert.Contains("可用", statuses);
            Assert.Contains("不支持", statuses);
        });
    }

    [Fact]
    public void AboutForm_TipsTab_HasContent()
    {
        Ui.RunWithAbout(form =>
        {
            var tips = Ui.Require<RichTextBox>(form, "tipsBox");
            Assert.Contains("参数优先级", tips.Text);
            Assert.Contains("码率怎么写", tips.Text);
            Assert.Contains("日志", tips.Text);
        });
    }

    [Fact]
    public void AboutForm_HasCloseButton()
    {
        Ui.RunWithAbout(form =>
        {
            var close = Ui.Require<Button>(form, "btnAboutClose");
            Assert.Equal(DialogResult.OK, close.DialogResult);
        });
    }

    /// <summary>说明窗口也应能正常渲染（不是空白框）。</summary>
    [Fact]
    public void AboutForm_RendersNonBlankScreenshot()
    {
        Ui.RunWithAbout(form =>
        {
            var path = Ui.CaptureWindow(form, "06-about");
            Assert.True(File.Exists(path));

            using var bmp = new Bitmap(path);
            Assert.True(Ui.CountDistinctColors(bmp) >= 40,
                $"说明窗口疑似空白: {path}");
        });
    }
}
