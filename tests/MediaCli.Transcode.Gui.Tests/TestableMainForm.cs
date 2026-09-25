using System.Drawing.Imaging;
using MediaCli.Transcode.Gui;

namespace MediaCli.Transcode.Gui.Tests;

/// <summary>
/// 驱动真实 <see cref="MainForm"/> 的测试替身。
///
/// 只覆写两处模态对话框钩子：真实 MessageBox 会阻塞调用线程，
/// 无人值守的自动化撞上就永久挂起。其余行为（布局、事件、映射）全部走生产代码。
/// 同时记录弹窗内容，使"是否给了用户提示"本身也可断言。
/// </summary>
internal sealed class TestableMainForm : MainForm
{
    public List<string> Confirms { get; } = [];
    public List<string> Notifications { get; } = [];

    /// <summary>Confirm 的返回值；默认 true（相当于用户点"确定/是"）。</summary>
    public bool ConfirmResult { get; set; } = true;

    protected override bool Confirm(string message, string title)
    {
        Confirms.Add($"{title}: {message}");
        return ConfirmResult;
    }

    protected override void Notify(string message, string title) =>
        Notifications.Add($"{title}: {message}");
}
