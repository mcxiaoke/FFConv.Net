using System.Drawing.Imaging;
using MediaCli.Transcode.Gui;
using Xunit;

namespace MediaCli.Transcode.Gui.Tests;

/// <summary>
/// 截图测试：把真实窗体渲染成 PNG，供人工复核与回归比对。
///
/// 单靠"能截图"不足以说明问题——未渲染的窗体会截出一块纯色，看起来也像成功。
/// 因此每个用例都断言图像包含足够多的不同颜色，确保确实渲染出了内容。
/// 截图落在仓库 temp/screenshots/（已被 .gitignore 覆盖，不进版本库）。
/// </summary>
public class MainFormScreenshotTests
{
    /// <summary>渲染出的图像至少要有的颜色数（纯色/空窗体远低于此值）。</summary>
    private const int MinDistinctColors = 40;

    private static string CaptureAndVerify(TestableMainForm form, string name)
    {
        var path = Ui.CaptureWindow(form, name);
        Assert.True(File.Exists(path), $"截图未生成: {path}");

        using var bmp = new Bitmap(path);
        Assert.True(bmp.Width > 100 && bmp.Height > 100,
            $"截图尺寸异常: {bmp.Width}x{bmp.Height}");

        var colors = Ui.CountDistinctColors(bmp);
        Assert.True(colors >= MinDistinctColors,
            $"截图疑似空白（仅 {colors} 种颜色）: {path}");

        return path;
    }

    /// <summary>默认状态：空输入、auto 参数、就绪。</summary>
    [Fact]
    public void Screenshot_DefaultState()
    {
        Ui.RunWithForm(form =>
        {
            Ui.WaitUntil(() => Ui.Require<ComboBox>(form, "presetCombo").Items.Count > 0, 30_000);
            Ui.Pump(600); // 等启动诊断写入日志

            var path = CaptureAndVerify(form, "01-default");
            Assert.Contains("已加载", Ui.Require<RichTextBox>(form, "logBox").Text);
            Assert.True(new FileInfo(path).Length > 5_000, "PNG 体积过小，可能未渲染");
        });
    }

    /// <summary>填好输入与输出、选定参数后的状态。</summary>
    [Fact]
    public void Screenshot_WithInputAndOptions()
    {
        var sample = Ui.TryCreateSampleVideo();
        Assert.True(sample is not null, "未找到 ffmpeg，无法生成样本素材");

        Ui.RunWithForm(form =>
        {
            Ui.WaitUntil(() => Ui.Require<ComboBox>(form, "presetCombo").Items.Count > 0, 30_000);

            Ui.Require<TextBox>(form, "inputBox").Text = sample!;
            Ui.Require<TextBox>(form, "outputBox").Text = Path.Combine(Path.GetTempPath(), "ffconvnet-out");
            Ui.Require<TextBox>(form, "ffargsBox").Text = "vb=3000000,vq=23";
            Ui.Require<CheckBox>(form, "debugCheck").Checked = true;
            Ui.Require<RadioButton>(form, "modeTree").Checked = true;

            Ui.Require<ComboBox>(form, "presetCombo").SelectedItem = "h264_2k";
            Ui.Require<ComboBox>(form, "hwaccelCombo").SelectedItem = "cuda";

            Ui.Pump(800); // 等输入扫描状态更新

            CaptureAndVerify(form, "02-input-filled");

            // 输入计数应更新到状态栏（验证交互确实生效）
            var status = Ui.Require<Label>(form, "statusLabel").Text;
            Assert.Contains("已收集", status);
        });
    }

    /// <summary>预览执行中/完成后的状态：日志有内容、进度条推进。</summary>
    [Fact]
    public void Screenshot_AfterPreview()
    {
        var sample = Ui.TryCreateSampleVideo();
        Assert.True(sample is not null, "未找到 ffmpeg，无法生成样本素材");

        Ui.RunWithForm(form =>
        {
            Ui.WaitUntil(() => Ui.Require<ComboBox>(form, "presetCombo").Items.Count > 0, 30_000);

            Ui.Require<TextBox>(form, "inputBox").Text = sample!;
            Ui.Require<TextBox>(form, "outputBox").Text = Path.Combine(Path.GetTempPath(), "ffconvnet-out");

            Ui.Click(form, "btnPreview");
            Assert.True(Ui.WaitUntil(() => !form.IsRunning && LogText(form).Contains("ffmpeg -"), 180_000),
                "预览未在超时内完成");

            Ui.Pump(700);

            CaptureAndVerify(form, "03-after-preview");

            var log = LogText(form);
            Assert.Contains("预览", log);
            Assert.Equal(100, Ui.Require<ProgressBar>(form, "progressBar").Value);
        });
    }

    /// <summary>校验告警弹窗路径：确认框被触发（此处用替身记录，不真弹窗）。</summary>
    [Fact]
    public void Screenshot_WithInvalidFfargsWarning()
    {
        var sample = Ui.TryCreateSampleVideo();
        Assert.True(sample is not null, "未找到 ffmpeg，无法生成样本素材");

        Ui.RunWithForm(form =>
        {
            Ui.WaitUntil(() => Ui.Require<ComboBox>(form, "presetCombo").Items.Count > 0, 30_000);

            Ui.Require<TextBox>(form, "inputBox").Text = sample!;
            Ui.Require<TextBox>(form, "ffargsBox").Text = "vb=3M,an=1";
            form.ConfirmResult = true;

            Ui.Click(form, "btnPreview");
            Assert.True(Ui.WaitUntil(() => !form.IsRunning && LogText(form).Contains("裸 bps"), 180_000),
                "预览未在超时内完成");

            Ui.Pump(700);

            CaptureAndVerify(form, "04-ffargs-warning");

            // 两条告警都应出现在确认框：带单位码率 + an 是空操作
            Assert.Single(form.Confirms);
            Assert.Contains("裸 bps", form.Confirms[0]);
            Assert.Contains("动漫模式", form.Confirms[0]);
        });
    }

    /// <summary>执行中状态：运行按钮禁用、取消可用、输入锁定。</summary>
    [Fact]
    public void Screenshot_WhileRunning()
    {
        var sample = Ui.TryCreateSampleVideo(seconds: 8, width: 1280, height: 720);
        Assert.True(sample is not null, "未找到 ffmpeg，无法生成样本素材");

        Ui.RunWithForm(form =>
        {
            Ui.WaitUntil(() => Ui.Require<ComboBox>(form, "presetCombo").Items.Count > 0, 30_000);

            Ui.Require<TextBox>(form, "inputBox").Text = sample!;
            Ui.Require<TextBox>(form, "outputBox").Text =
                Path.Combine(Path.GetTempPath(), "ffconvnet-out-running");
            Ui.Require<CheckBox>(form, "overrideCheck").Checked = true;

            Ui.Click(form, "btnRun");

            // 抓"运行中"这一瞬：按钮态已切换、任务尚未结束
            Assert.True(Ui.WaitUntil(() => form.IsRunning, 60_000), "任务未进入运行状态");
            Ui.Pump(500);

            Assert.False(Ui.Require<Button>(form, "btnRun").Enabled, "运行中「开始转码」应禁用");
            Assert.True(Ui.Require<Button>(form, "btnCancel").Enabled, "运行中「取消」应可用");
            Assert.True(Ui.Require<TextBox>(form, "inputBox").ReadOnly, "运行中输入框应只读");

            CaptureAndVerify(form, "05-running");

            // 收尾：取消并等待，避免留下孤儿 ffmpeg 进程
            Ui.Click(form, "btnCancel");
            Assert.True(Ui.WaitUntil(() => !form.IsRunning, 120_000), "取消后任务未结束");
            Ui.Pump(400);
        });
    }

    private static string LogText(MainForm form) =>
        Ui.Require<RichTextBox>(form, "logBox").Text;
}
