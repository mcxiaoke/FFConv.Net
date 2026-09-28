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

    /// <summary>
    /// 回归 P0-1：关闭按钮曾被 Dock=Fill 的 TabControl 完全遮挡——
    /// TabControl 区域覆盖整个客户区，且 z-order 上位于按钮之上，
    /// 运行期按钮不可见也点不到（只能靠标题栏 ✕ 关闭，Esc/Enter 亦无效）。
    /// 这里用真实命中测试锁定：按钮中心点必须由按钮自己接住。
    /// </summary>
    [Fact]
    public void AboutForm_CloseButton_IsActuallyClickable()
    {
        Ui.RunWithAbout(form =>
        {
            var close = Ui.Require<Button>(form, "btnAboutClose");
            var tabs = Ui.Require<TabControl>(form, "aboutTabs");

            // 按钮必须可见且不与 TabControl 区域重叠
            Assert.True(close.Visible, "关闭按钮不可见");
            Assert.True(close.Width > 0 && close.Height > 0, "关闭按钮尺寸为 0");

            var closeScreen = close.Parent!.RectangleToScreen(close.Bounds);
            var tabsScreen = tabs.Parent!.RectangleToScreen(tabs.Bounds);
            Assert.False(tabsScreen.IntersectsWith(closeScreen),
                "TabControl 与关闭按钮区域重叠，按钮会被遮挡");

            // 真实命中测试：按钮中心点必须落在按钮上
            var center = new Point(close.Width / 2, close.Height / 2);
            var hit = close.Parent.GetChildAtPoint(
                close.Parent.PointToClient(close.PointToScreen(center)));
            Assert.True(ReferenceEquals(hit, close),
                $"关闭按钮被 \"{hit?.Name ?? "null"}\" 遮挡，用户点不到");

            // Esc / Enter 也应能关闭（CancelButton / AcceptButton）
            Assert.True(ReferenceEquals(form.CancelButton, close) || form.CancelButton is not null,
                "关闭按钮未接入 Esc 关闭路径");
        });
    }

    /// <summary>
    /// 回归 P2-1：ListView 列宽不会被 AutoScaleDimensions 缩放，
    /// 需在 Shown 时按 DPI 换算，否则 150% 下长说明文本被截成省略号。
    /// 「说明」列使用 -2 自适应标记，必须原样保留而不是被换算成固定像素。
    /// </summary>
    [Fact]
    public void AboutForm_ListViewColumns_AreScaledForDpi()
    {
        Ui.RunWithAbout(form =>
        {
            var list = Ui.Require<ListView>(form, "cliOptionList");
            var factor = form.DeviceDpi / 96.0;
            var cols = list.Columns.Cast<ColumnHeader>().ToDictionary(c => c.Text, c => c);

            // 固定列按 DPI 换算（96 DPI 因子为 1，保持设计值）
            Assert.Equal((int)Math.Round(230 * factor), cols["参数"].Width);
            Assert.Equal((int)Math.Round(110 * factor), cols["取值"].Width);
            Assert.Equal((int)Math.Round(80 * factor), cols["状态"].Width);

            // 自适应列必须仍然"自适应"，不能被换算成固定宽度
            Assert.True(cols["说明"].Width > 0, "说明列应已由 WinForms 按其内容算出实际宽度");

            if (factor > 1.0)
            {
                Assert.True(cols["参数"].Width > 230, "高 DPI 下固定列宽未放大");
            }
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

    /// <summary>
    /// 使用提示里出现的每个 <c>--参数</c> 都必须真实存在。
    ///
    /// 这条挡的是"文档漂移"：示例曾写成 <c>--max-bitrate</c>，而
    /// <see cref="CliOptions"/> 里根本没有这个参数，用户照抄只会得到
    /// "未知参数，已忽略"——比没有示例更糟。
    /// 参数表是单一事实源，示例必须与它一致。
    /// </summary>
    [Fact]
    public void UsageTips_OnlyMentionsDefinedParams()
    {
        var text = AboutContent.UsageTips();

        var unknown = System.Text.RegularExpressions.Regex
            .Matches(text, @"--[a-zA-Z][a-zA-Z0-9-]*")
            .Select(m => m.Value[2..])
            .Distinct(StringComparer.Ordinal)
            .Where(name => CliOptions.Resolve(name) is null)
            .ToList();

        Assert.True(unknown.Count == 0,
            "使用提示引用了未定义的参数（用户照抄会得到「未知参数」）: " +
            string.Join(", ", unknown.Select(n => "--" + n)));
    }

    /// <summary>参数说明表的每一行都必须能被解析回来（说明与解析器同源）。</summary>
    [Fact]
    public void CliOptionTable_OnlyMentionsDefinedParams()
    {
        var text = AboutContent.CliOptionTable();

        var unknown = System.Text.RegularExpressions.Regex
            .Matches(text, @"--[a-zA-Z][a-zA-Z0-9-]*")
            .Select(m => m.Value[2..])
            .Distinct(StringComparer.Ordinal)
            .Where(name => CliOptions.Resolve(name) is null)
            .ToList();

        Assert.True(unknown.Count == 0,
            "参数说明表引用了未定义的参数: " + string.Join(", ", unknown.Select(n => "--" + n)));
    }
}
