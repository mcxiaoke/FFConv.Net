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
            Assert.Contains("av1_2k", items);
            // _base_* 是继承基类，不应注册为可用预设
            Assert.DoesNotContain(items, i => i.StartsWith('_'));
            Assert.Equal("av1_2k", combo.SelectedItem);
        });
    }

    /// <summary>启动诊断应在日志里说明实际使用的 ffmpeg 与硬件能力，并打印默认预设详情。</summary>
    [Fact]
    public void Startup_LogsFfmpegPathAndHardware()
    {
        Ui.RunWithForm(form =>
        {
            Assert.True(Ui.WaitUntil(() => LogText(form).Contains("ffmpeg:"), 30_000),
                "日志应报告实际使用的 ffmpeg 路径");
            Assert.Contains("ffprobe:", LogText(form));
            Assert.True(Ui.WaitUntil(() => LogText(form).Contains("预设详情 [av1_2k]"), 30_000),
                "日志应打印默认预设 av1_2k 详情");
        });
    }

    /// <summary>修改预设时应在日志区打印新预设的详细信息。</summary>
    [Fact]
    public void ChangingPreset_LogsPresetDetail()
    {
        Ui.RunWithForm(form =>
        {
            var combo = Ui.Require<ComboBox>(form, "presetCombo");
            Assert.True(Ui.WaitUntil(() => combo.Items.Count > 0, 30_000));
            combo.SelectedItem = "h264_2k";
            Ui.Pump(200);
            Assert.Contains("预设详情 [h264_2k]", LogText(form));
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
            // 需求：日志自动换行、不要横向滚动条（命令行长，换行比横向拖动易读）
            Assert.True(log.WordWrap, "日志应启用自动换行");
            Assert.Equal(RichTextBoxScrollBars.Vertical, log.ScrollBars);
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
            Ui.Require<TextBox>(form, "cliArgsBox").Text = "--ffargs vb=3M";
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
            Ui.Require<TextBox>(form, "cliArgsBox").Text = "--ffargs vb=3M";
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
            Ui.Require<TextBox>(form, "cliArgsBox").Text = "--ffargs vb=3000000,vq=23";

            Ui.Click(form, "btnPreview");
            Assert.True(Ui.WaitUntil(() => !form.IsRunning && LogText(form).Contains("ffmpeg -"),
                180_000), "预览结束后日志里应出现命令行");

            Assert.Empty(form.Confirms);
        });
    }

    /// <summary>
    /// 参数框已改为只读回显：真值由「高级参数」面板写回，界面不再靠占位符教语法。
    ///
    /// 这条覆盖的是同一个缺陷（曾把 <c>vb=3M</c> 这种<b>不能生效</b>的写法当作示例），
    /// 只是修法从"把占位符改对"升级为"不再有可自由输入的入口"。
    /// 断言保留两部分：主界面回显不再教任何写法；面板里也不出现该错误示例。
    /// </summary>
    [Fact]
    public void CliArgsBox_IsReadOnlyEcho_AndTeachesNoInvalidSyntax()
    {
        Ui.RunWithForm(form =>
        {
            var box = Ui.Require<TextBox>(form, "cliArgsBox");

            Assert.True(box.ReadOnly, "参数框应为只读回显，避免与面板形成两个真值源");
            Assert.True(string.IsNullOrWhiteSpace(box.PlaceholderText),
                "只读回显不应再有占位示例，示例一律走高级参数面板");

            using var panel = new ParamsForm("hevc_2k", "");
            Assert.DoesNotContain("vb=3M", ParamsFormScanAllText(panel));
        });
    }

    /// <summary>遍历面板控件树，收集全部可见文本（用于"界面有没有教错写法"这类断言）。</summary>
    private static string ParamsFormScanAllText(Control root)
    {
        var sb = new System.Text.StringBuilder();
        foreach (var c in Ui.AllControls(root)) sb.AppendLine(c.Text);
        return sb.ToString();
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

    /// <summary>
    /// 先点预览再点转码：转码完成后进度条必须为 100%，且不会被后置 Flush 覆盖或变色滞后。
    /// </summary>
    [Fact]
    public void PreviewThenTranscode_ProgressBarReaches100Percent()
    {
        var sample = RequireSample();

        Ui.RunWithForm(form =>
        {
            Ui.WaitUntil(() => Ui.Require<ComboBox>(form, "presetCombo").Items.Count > 0, 30_000);

            Ui.Require<TextBox>(form, "inputBox").Text = sample;
            var outDir = Path.Combine(Path.GetTempPath(), "ffconv-test-prevrun-" + Guid.NewGuid().ToString("N")[..8]);
            Ui.Require<TextBox>(form, "outputBox").Text = outDir;
            Ui.Require<CheckBox>(form, "overrideCheck").Checked = true;
            form.ConfirmResult = true;

            // 1. 先点预览
            Ui.Click(form, "btnPreview");
            Assert.True(Ui.WaitUntil(() => !form.IsRunning, 180_000), "预览未在超时内完成");
            Ui.Pump(300);
            Assert.Equal(100, Ui.Require<ProgressBar>(form, "progressBar").Value);

            // 2. 再点转码
            Ui.Click(form, "btnRun");
            Assert.True(Ui.WaitUntil(() => !form.IsRunning, 180_000), "转码未在超时内完成");
            Ui.Pump(600); // 等待 Flush 充分执行

            var bar = Ui.Require<ProgressBar>(form, "progressBar");
            Assert.Equal(100, bar.Value);
            var label = Ui.Require<Label>(form, "progressLabel");
            Assert.Contains("完成 1/1", label.Text);

            // 并且环境信息与预设信息已在转码开始后重新打印在日志区域
            var log = LogText(form);
            Assert.Contains("=== 开始转码 ===", log);
            Assert.Contains("预设详情", log);

            try { Directory.Delete(outDir, true); } catch { /* ignore */ }
        });
    }

    // ------------------------------------------------------------------
    // UI/UX 增强测试
    // ------------------------------------------------------------------

    [Fact]
    public void DragDrop_IsConfigured()
    {
        Ui.RunWithForm(form =>
        {
            Assert.True(form.AllowDrop, "窗体应启用 AllowDrop");
            var input = Ui.Require<TextBox>(form, "inputBox");
            Assert.True(input.AllowDrop, "输入框应启用 AllowDrop");
            var output = Ui.Require<TextBox>(form, "outputBox");
            Assert.True(output.AllowDrop, "输出框应启用 AllowDrop");
        });
    }

    [Fact]
    public void ActionButtons_HaveAccessKeysAndAccessibleNames()
    {
        Ui.RunWithForm(form =>
        {
            var btnRun = Ui.Require<Button>(form, "btnRun");
            Assert.Contains("&R", btnRun.Text);
            Assert.Equal("开始转码", btnRun.AccessibleName);

            var btnPreview = Ui.Require<Button>(form, "btnPreview");
            Assert.Contains("&P", btnPreview.Text);
            Assert.Equal("预览命令", btnPreview.AccessibleName);

            var btnCancel = Ui.Require<Button>(form, "btnCancel");
            Assert.Contains("&C", btnCancel.Text);
            Assert.Equal("取消", btnCancel.AccessibleName);

            var btnClear = Ui.Require<Button>(form, "btnClear");
            Assert.Contains("&L", btnClear.Text);
            Assert.Equal("清空日志", btnClear.AccessibleName);

            var btnOpenOutput = Ui.Require<Button>(form, "btnOpenOutput");
            Assert.Contains("&O", btnOpenOutput.Text);
            Assert.Equal("打开输出目录", btnOpenOutput.AccessibleName);
        });
    }

    [Fact]
    public void ContextMenus_AreConfigured()
    {
        Ui.RunWithForm(form =>
        {
            var input = Ui.Require<TextBox>(form, "inputBox");
            Assert.NotNull(input.ContextMenuStrip);
            Assert.True(input.ContextMenuStrip.Items.Count >= 3);

            var log = Ui.Require<RichTextBox>(form, "logBox");
            Assert.NotNull(log.ContextMenuStrip);
            Assert.True(log.ContextMenuStrip.Items.Count >= 4);
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
