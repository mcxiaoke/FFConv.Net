using MediaCli.Transcode.Gui;
using Xunit;

namespace MediaCli.Transcode.Gui.Tests;

/// <summary>
/// 输入解析、参数校验与提示类交互。
///
/// 这些用例驱动的是<b>显示中的真实窗体</b>（见 <see cref="Ui.RunWithForm"/>）：
/// 未显示的窗体上 <c>Button.PerformClick</c> 会静默失败，测试将失去意义。
///
/// 重点覆盖一条真实缺陷的修复：<c>FfargsValidator</c> 早已实现告警，但
/// <c>StartRun</c> 从未展示它们——用户填了 <c>vb=3M</c>（core 会静默丢弃）却看不到任何提示。
/// 这里把"必须给用户提示"变成可断言的契约。
/// </summary>
public class MainFormInputTests
{
    // ------------------------------------------------------------------
    // 默认值与初始状态
    // ------------------------------------------------------------------

    [Fact]
    public void Defaults_AreSafe()
    {
        Ui.RunWithForm(form =>
        {
            Assert.Equal("auto", Ui.Require<ComboBox>(form, "hwaccelCombo").SelectedItem);
            Assert.Equal("auto", Ui.Require<ComboBox>(form, "decodeCombo").SelectedItem);
            Assert.True(Ui.Require<RadioButton>(form, "modeDir").Checked, "输出模式默认应为 dir");
            Assert.False(Ui.Require<RadioButton>(form, "modeTree").Checked);
            Assert.False(Ui.Require<RadioButton>(form, "modeFile").Checked);

            // 默认不覆盖已有产物 —— 与 core 契约一致
            Assert.False(Ui.Require<CheckBox>(form, "overrideCheck").Checked);
            Assert.False(Ui.Require<CheckBox>(form, "strictCheck").Checked);
            Assert.False(Ui.Require<CheckBox>(form, "debugCheck").Checked);
            Assert.False(Ui.Require<CheckBox>(form, "animeCheck").Checked);

            // 未运行时取消按钮不可用
            Assert.False(Ui.Require<Button>(form, "btnCancel").Enabled);
            Assert.True(Ui.Require<Button>(form, "btnRun").Enabled);
            Assert.True(Ui.Require<Button>(form, "btnPreview").Enabled);
            Assert.False(form.IsRunning);
        });
    }

    /// <summary>启动诊断应把预设填进下拉框（真实启动路径，Shown 事件触发）。</summary>
    [Fact]
    public void Startup_PopulatesPresetCombo()
    {
        Ui.RunWithForm(form =>
        {
            var combo = Ui.Require<ComboBox>(form, "presetCombo");
            Assert.True(Ui.WaitUntil(() => combo.Items.Count > 0, 30_000),
                "启动后预设下拉框应被填充");

            var items = combo.Items.Cast<string>().ToList();
            Assert.Contains("hevc_2k", items);
            Assert.Contains("h264_2k", items);
            // _base_* 是继承基类，不应注册为可用预设
            Assert.DoesNotContain(items, i => i.StartsWith('_'));
            Assert.Equal("hevc_2k", combo.SelectedItem);
        });
    }

    /// <summary>启动诊断应在日志里说明实际使用的 ffmpeg 与硬件能力。</summary>
    [Fact]
    public void Startup_LogsFfmpegPathAndHardware()
    {
        Ui.RunWithForm(form =>
        {
            Assert.True(Ui.WaitUntil(() => LogText(form).Contains("ffmpeg:"), 30_000),
                "日志应报告实际使用的 ffmpeg 路径");
            Assert.Contains("ffprobe:", LogText(form));
        });
    }

    [Fact]
    public void ComboBoxOptions_AreComplete()
    {
        Ui.RunWithForm(form =>
        {
            var hw = Ui.Require<ComboBox>(form, "hwaccelCombo").Items.Cast<string>().ToList();
            foreach (var expected in new[] { "auto", "cuda", "qsv", "amf", "d3d", "d3d11va", "d3d12va", "dxva2", "cpu" })
            {
                Assert.Contains(expected, hw);
            }

            var dec = Ui.Require<ComboBox>(form, "decodeCombo").Items.Cast<string>().ToList();
            Assert.Equal(["auto", "gpu", "cpu"], dec);

            // 下拉框必须是只读列表，否则用户能输入 core 不认识的取值
            Assert.Equal(ComboBoxStyle.DropDownList, Ui.Require<ComboBox>(form, "hwaccelCombo").DropDownStyle);
            Assert.Equal(ComboBoxStyle.DropDownList, Ui.Require<ComboBox>(form, "decodeCombo").DropDownStyle);
            Assert.Equal(ComboBoxStyle.DropDownList, Ui.Require<ComboBox>(form, "presetCombo").DropDownStyle);
        });
    }

    /// <summary>日志面板必须只读——用户不应能编辑日志内容。</summary>
    [Fact]
    public void LogBox_IsReadOnly()
    {
        Ui.RunWithForm(form =>
        {
            var log = Ui.Require<RichTextBox>(form, "logBox");
            Assert.True(log.ReadOnly);
            Assert.False(log.WordWrap, "日志应禁用自动换行以便阅读命令行");
        });
    }

    // ------------------------------------------------------------------
    // 缺少输入时的提示
    // ------------------------------------------------------------------

    [Fact]
    public void Preview_WithoutInput_NotifiesUser()
    {
        Ui.RunWithForm(form =>
        {
            Ui.Click(form, "btnPreview");

            Assert.Single(form.Notifications);
            Assert.Contains("请先选择输入", form.Notifications[0]);
            Assert.False(form.IsRunning, "缺少输入时不应启动任务");
        });
    }

    // ------------------------------------------------------------------
    // ffargs 告警必须浮出水面（缺陷修复的回归测试）
    // ------------------------------------------------------------------

    /// <summary>
    /// 带单位的码率会被 core 静默丢弃，因此必须在开跑前提示用户。
    /// 这条用例直接锁定"绝不静默吞掉"这一设计承诺。
    /// </summary>
    [Fact]
    public void Preview_WithUnitSuffixedBitrate_WarnsUserBeforeRunning()
    {
        var sample = RequireSample();
        Ui.RunWithForm(form =>
        {
            Ui.Require<TextBox>(form, "inputBox").Text = sample;
            Ui.Require<TextBox>(form, "ffargsBox").Text = "vb=3M";
            form.ConfirmResult = false;   // 用户看到提示后选择放弃

            Ui.Click(form, "btnPreview");

            Assert.Single(form.Confirms);
            Assert.Contains("不会生效", form.Confirms[0]);
            Assert.Contains("裸 bps", form.Confirms[0]);
            Assert.False(form.IsRunning, "用户取消后不应启动任务");
        });
    }

    /// <summary>用户确认继续时，告警也要留在日志里，便于事后追溯。</summary>
    [Fact]
    public void Preview_WithInvalidFfargs_KeepsWarningInLog()
    {
        var sample = RequireSample();
        Ui.RunWithForm(form =>
        {
            Ui.Require<TextBox>(form, "inputBox").Text = sample;
            Ui.Require<TextBox>(form, "ffargsBox").Text = "vb=3M";
            form.ConfirmResult = true;

            Ui.Click(form, "btnPreview");
            Assert.True(Ui.WaitUntil(() => !form.IsRunning && LogText(form).Contains("裸 bps"), 180_000),
                "预览结束后日志里应保留 ffargs 告警");

            Assert.False(form.IsRunning);
        });
    }

    /// <summary>合法参数不应产生任何告警（避免过度提示导致用户忽略提示）。</summary>
    [Fact]
    public void Preview_WithValidFfargs_DoesNotWarn()
    {
        var sample = RequireSample();
        Ui.RunWithForm(form =>
        {
            Ui.Require<TextBox>(form, "inputBox").Text = sample;
            Ui.Require<TextBox>(form, "ffargsBox").Text = "vb=3000000,vq=23";

            Ui.Click(form, "btnPreview");
            Assert.True(Ui.WaitUntil(() => !form.IsRunning && LogText(form).Contains("ffmpeg -"),
                180_000), "预览结束后日志里应出现命令行");

            Assert.Empty(form.Confirms);
        });
    }

    /// <summary>占位提示本身不能教用户写错的语法（曾写成 vb=3M）。</summary>
    [Fact]
    public void FfargsPlaceholder_DoesNotTeachInvalidSyntax()
    {
        Ui.RunWithForm(form =>
        {
            var placeholder = Ui.Require<TextBox>(form, "ffargsBox").PlaceholderText;

            Assert.False(string.IsNullOrWhiteSpace(placeholder));
            // 带单位的码率写法会被 core 静默丢弃，不能作为示例
            Assert.DoesNotContain("vb=3M", placeholder);
            Assert.Contains("vb=", placeholder);
        });
    }

    // ------------------------------------------------------------------
    // 清空日志
    // ------------------------------------------------------------------

    [Fact]
    public void ClearLog_EmptiesLogPanelAndResetsProgress()
    {
        Ui.RunWithForm(form =>
        {
            var log = Ui.Require<RichTextBox>(form, "logBox");
            var bar = Ui.Require<ProgressBar>(form, "progressBar");

            log.Text = "一些日志";
            bar.Value = 50;

            Ui.Click(form, "btnClear");

            Assert.Equal("", log.Text);
            Assert.Equal(0, bar.Value);
        });
    }

    // ------------------------------------------------------------------
    // 辅助
    // ------------------------------------------------------------------

    private static string LogText(MainForm form) =>
        Ui.Require<RichTextBox>(form, "logBox").Text;

    /// <summary>
    /// 取得样本视频。
    ///
    /// 不用"条件跳过"：xunit v2 没有运行时跳过 API，而静默 return 会让测试
    /// 在缺少 ffmpeg 时假装通过——那是假绿。ffmpeg 是本项目的运行前提
    /// （core 的全部功能都依赖它），缺失时明确失败才是正确的信号。
    /// </summary>
    private static string RequireSample()
    {
        var sample = Ui.TryCreateSampleVideo();
        Assert.True(sample is not null,
            "未找到 ffmpeg：这些 UI 用例需要真实转码能力。" +
            "请设置 FFMPEG_PATH 环境变量，或把 ffmpeg 加入 PATH。");
        return sample!;
    }
}
