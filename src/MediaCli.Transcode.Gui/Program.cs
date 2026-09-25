namespace MediaCli.Transcode.Gui;

/// <summary>FFConv GUI 入口。</summary>
internal static class Program
{
    [STAThread]
    private static void Main()
    {
        ApplicationConfiguration.Initialize();

        // 全局兜底：未处理异常至少要让用户看到原因，而不是静默消失。
        Application.ThreadException += (_, e) => ReportFatal(e.Exception);
        AppDomain.CurrentDomain.UnhandledException += (_, e) => ReportFatal(e.ExceptionObject as Exception);
        TaskScheduler.UnobservedTaskException += (_, e) =>
        {
            ReportFatal(e.Exception);
            e.SetObserved();
        };

        try
        {
            Application.Run(new MainForm());
        }
        catch (Exception ex)
        {
            ReportFatal(ex);
        }
    }

    private static void ReportFatal(Exception? ex)
    {
        try
        {
            MessageBox.Show(
                ex?.ToString() ?? "未知错误",
                "FFConv — 未处理异常",
                MessageBoxButtons.OK,
                MessageBoxIcon.Error);
        }
        catch
        {
            // 连弹窗都失败时无能为力，避免二次异常掩盖原始错误
        }
    }
}
