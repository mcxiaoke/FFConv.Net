using System.Diagnostics;
using System.Drawing.Imaging;
using Xunit;

// UI 测试会创建窗体、读写控件并在屏幕上截图；并行执行会互相干扰
// （多个窗体争前台、静态的 FfmpegRun.ffmpegPath 被并发改写）。
[assembly: CollectionBehavior(DisableTestParallelization = true)]

namespace MediaCli.Transcode.Gui.Tests;

/// <summary>
/// UI 测试基础设施：STA 执行器、消息泵、屏幕/控件截图、样本素材。
///
/// 设计取舍：测试<b>驱动真实的 <see cref="MainForm"/></b>，而不是它的某个替身。
/// 控件通过 <c>Name</c> 查找（见 <see cref="Find"/>），与真实 UI 自动化的做法一致，
/// 因此不依赖坐标或私有字段，布局调整不会误伤测试。
/// </summary>
internal static class Ui
{
    /// <summary>截图输出目录（仓库 temp/ 已被 .gitignore 覆盖）。</summary>
    public static string ScreenshotDir { get; } = ResolveScreenshotDir();

    private static string ResolveScreenshotDir()
    {
        // bin/Debug/net8.0-windows → 仓库根
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "MediaCli.Transcode.slnx")))
        {
            dir = dir.Parent;
        }
        var root = dir?.FullName ?? AppContext.BaseDirectory;
        var target = Path.Combine(root, "temp", "screenshots");
        Directory.CreateDirectory(target);
        return target;
    }

    /// <summary>
    /// 在独立 STA 线程上执行 UI 操作，并把异常回传播出。
    /// WinForms 需要 STA：对话框、OLE、剪贴板等在多线程单元下行为不确定。
    ///
    /// 同时把进程的 DPI 感知模式对齐到 GUI 应用（PerMonitorV2）。
    /// 这一步不可省略：若测试进程是 DPI 未感知的，Windows 会整体缩放窗口位图，
    /// 应用自身仍按 96 DPI 绘制，于是"文字被裁切"这类缺陷在测试里<b>根本不会出现</b>——
    /// 而真实 exe 在 150% 缩放的屏幕上会立刻暴露。实测本机为 4K@150%，
    /// 对齐前测试全绿、exe 截图文字被裁，正是这个原因。
    /// </summary>
    /// <param name="timeoutMs">
    /// STA 线程的等待上限。必须存在：UI 测试一旦撞上模态对话框（MessageBox 会阻塞调用线程）
    /// 或死锁，<c>Join()</c> 会永久等待，整个测试运行就此挂死——实测踩过一次，
    /// 10 分钟无任何输出。有了超时，这种情况会变成一条明确的失败。
    /// </param>
    public static void RunInSta(Action action, int timeoutMs = 240_000)
    {
        EnsureDpiAwareness();

        Exception? captured = null;
        var thread = new Thread(() =>
        {
            try
            {
                Application.EnableVisualStyles();
                action();
            }
            catch (Exception ex)
            {
                captured = ex;
            }
        });
        thread.SetApartmentState(ApartmentState.STA);
        // 后台线程：即使真的卡死，也不阻止测试进程退出
        thread.IsBackground = true;
        thread.Start();

        if (!thread.Join(timeoutMs))
        {
            throw new TimeoutException(
                $"STA UI 线程在 {timeoutMs} ms 内未结束：疑似模态对话框阻塞或死锁。" +
                "检查用例里是否弹出了 MessageBox（UI 测试必须走可覆写的钩子）。");
        }

        if (captured is not null)
        {
            throw new InvalidOperationException("STA UI 线程异常: " + captured.Message, captured);
        }
    }

    private static int dpiAwarenessApplied;

    /// <summary>
    /// 对齐 DPI 感知（幂等；必须在任何窗口创建前调用）。
    /// 失败时不影响测试结论——只是测试环境与生产略有差异，由调用方自行判断。
    /// </summary>
    private static void EnsureDpiAwareness()
    {
        if (Interlocked.Exchange(ref dpiAwarenessApplied, 1) == 1) return;
        try
        {
            Application.SetHighDpiMode(HighDpiMode.PerMonitorV2);
        }
        catch (InvalidOperationException)
        {
            // 已有窗口创建过，DPI 感知已定；此时忽略即可
        }
    }

    /// <summary>当前进程的实际 DPI 缩放比例（1.0 = 96 DPI）。</summary>
    public static double CurrentScaleFactor()
    {
        using var g = Graphics.FromHwnd(IntPtr.Zero);
        return g.DpiX / 96.0;
    }

    /// <summary>系统 DPI（与进程是否感知无关，始终返回真实值）。</summary>
    public static int SystemDpi()
    {
        try
        {
            return (int)NativeMethods.GetDpiForSystem();
        }
        catch
        {
            return 96;
        }
    }

    /// <summary>
    /// 处理消息队列指定时长。WinForms 的 Timer 依赖消息循环才会触发，
    /// 因此测试里必须显式泵消息，否则日志刷新与状态更新永远不会发生。
    /// </summary>
    public static void Pump(int milliseconds)
    {
        var sw = Stopwatch.StartNew();
        while (sw.ElapsedMilliseconds < milliseconds)
        {
            Application.DoEvents();
            Thread.Sleep(5);
        }
    }

    /// <summary>
    /// 按 Name 递归查找控件（等价于 UI 自动化的查找键）。
    ///
    /// 同时覆盖 <see cref="ToolStripItem"/>：状态栏的标签不是 <see cref="Control"/>，
    /// 挂在 <c>StatusStrip.Items</c> 上，仅用 <c>Controls.Find</c> 永远找不到它们。
    /// </summary>
    public static Control? Find(Control root, string name)
    {
        var found = root.Controls.Find(name, searchAllChildren: true);
        if (found.Length > 0) return found[0];
        return null;
    }

    /// <summary>按 Name 查找状态栏项（ToolStripItem 不是 Control）。</summary>
    public static ToolStripItem? FindToolStripItem(Control root, string name)
    {
        foreach (var strip in AllControls(root).OfType<StatusStrip>())
        {
            foreach (ToolStripItem item in strip.Items)
            {
                if (string.Equals(item.Name, name, StringComparison.Ordinal)) return item;
            }
        }
        return null;
    }

    /// <summary>
    /// 读取状态栏项的文本。
    ///
    /// 必须经此封装：<c>ToolStripItem.Text</c> 在 .NET 8 中标注为 <c>string?</c>，
    /// 直接 <c>item.Text.Contains(...)</c> 会触发 CS8602（本项目把警告当错误）。
    /// </summary>
    public static string ItemText(ToolStripItem? item) => item?.Text ?? "";

    /// <summary>递归枚举控件树（含自身）。</summary>
    public static IEnumerable<Control> AllControls(Control root)
    {
        yield return root;
        foreach (Control c in root.Controls)
        {
            foreach (var d in AllControls(c)) yield return d;
        }
    }

    /// <summary>按 Name 查找控件或状态栏项，并断言存在。</summary>
    public static void RequireAny(Control root, string name)
    {
        var control = Find(root, name);
        var item = control is null ? FindToolStripItem(root, name) : null;
        Assert.True(control is not null || item is not null, $"控件/状态栏项缺失: {name}");
    }

    /// <summary>按 Name 查找并断言存在，返回强类型控件。</summary>
    public static T Require<T>(Control root, string name) where T : Control
    {
        var c = Find(root, name);
        Assert.True(c is not null, $"控件缺失: {name}");
        Assert.IsType<T>(c);
        return (T)c!;
    }

    /// <summary>
    /// 构造并显示窗体后执行测试体。
    ///
    /// 必须 Show()：<c>Button.PerformClick</c> 只在 <c>CanSelect</c> 为真时才触发 Click，
    /// 而 CanSelect 要求控件及其整条父链可见。在未显示的窗体上点击是**静默空操作**，
    /// 测试会表现为"什么都没发生"，很难排查。
    /// 显示窗体同时会触发 Shown 事件，让启动诊断等真实启动路径也被覆盖。
    /// </summary>
    public static void RunWithForm(Action<TestableMainForm> action)
    {
        RunInSta(() =>
        {
            using var form = new TestableMainForm();
            form.Show();
            Pump(400); // 等句柄创建 + Shown 事件里的启动诊断完成
            try
            {
                action(form);
            }
            finally
            {
                try { form.Close(); } catch { /* 关窗失败不影响测试结论 */ }
            }
        });
    }

    /// <summary>
    /// 构造并显示「使用说明」窗口后执行测试体。
    ///
    /// 与 <see cref="RunWithForm"/> 同理：必须 Show()，否则控件不可见、
    /// PerformClick 之类的交互会静默失效。
    /// </summary>
    public static void RunWithAbout(Action<AboutForm> action)
    {
        RunInSta(() =>
        {
            using var form = new AboutForm();
            form.Show();
            Pump(400);
            try
            {
                action(form);
            }
            finally
            {
                try { form.Close(); } catch { /* 关窗失败不影响测试结论 */ }
            }
        });
    }

    /// <summary>
    /// 构造并显示「高级参数」面板后执行测试体。
    ///
    /// 同样必须 Show()：否则 PerformClick 是静默空操作（见 <see cref="RunWithForm"/>）。
    /// </summary>
    public static void RunWithParams(Action<ParamsForm> action, string presetName = "hevc_2k", string cliArgs = "")
        => RunWithFormCore(() => new ParamsForm(presetName, cliArgs), action);

    public static void RunWithParams(Func<ParamsForm> factory, Action<ParamsForm> action)
        => RunWithFormCore(factory, action);

    public static void RunWithParamsFlow(Action<ParamsFlowForm> action, string presetName = "hevc_2k", string cliArgs = "")
        => RunWithFormCore(() => new ParamsFlowForm(presetName, cliArgs), action);

    public static void RunWithParamsFlow(Func<ParamsFlowForm> factory, Action<ParamsFlowForm> action)
        => RunWithFormCore(factory, action);

    private static void RunWithFormCore<T>(Func<T> factory, Action<T> action) where T : Form
    {
        RunInSta(() =>
        {
            using var form = factory();
            form.Show();
            Pump(400);
            try
            {
                action(form);
            }
            finally
            {
                try { form.Close(); } catch { /* 关窗失败不影响测试结论 */ }
            }
        });
    }

    /// <summary>点击按钮；先断言确实可点击，避免又一次静默空操作。</summary>
    public static void Click(Control root, string name)
    {
        var btn = Require<Button>(root, name);
        Assert.True(btn.CanSelect,
            $"按钮 {name} 不可点击（CanSelect=false）：窗体需已显示，且控件处于启用状态");
        btn.PerformClick();
    }

    /// <summary>
    /// 抓取窗体图像。
    ///
    /// 优先用 <c>PrintWindow</c>，而不是 <c>Graphics.CopyFromScreen</c>。
    /// 原因：CopyFromScreen 抓的是屏幕<b>物理像素</b>，要求目标窗口真的在那些坐标上可见。
    /// 测试进程不在前台时，Windows 的前台锁会拒绝 <c>SetForegroundWindow</c>，
    /// 窗体被其它窗口（如测试宿主的控制台）盖住，于是抓到的是别人的画面——
    /// 实测表现为三个不同状态的截图 SHA256 完全相同、且深色像素占一半。
    /// PrintWindow 让窗口把自己画到指定 DC，与 z 序和焦点无关，因此结果稳定。
    /// </summary>
    public static string CaptureWindow(Form form, string name)
    {
        form.Activate();
        form.BringToFront();
        form.Refresh();
        Pump(300);

        using var bmp = CaptureViaPrintWindow(form) ?? CaptureViaDrawToBitmap(form);
        AssertLooksLikeForm(bmp, name);
        return Save(bmp, name + ".png");
    }

    /// <summary>用 PrintWindow 让窗口自绘（含标题栏等非客户区）。</summary>
    private static Bitmap? CaptureViaPrintWindow(Form form)
    {
        if (!form.IsHandleCreated) return null;

        var size = form.Size;
        if (size.Width <= 0 || size.Height <= 0) return null;

        var bmp = new Bitmap(size.Width, size.Height);
        using var g = Graphics.FromImage(bmp);
        var hdc = g.GetHdc();
        try
        {
            // PW_RENDERFULLCONTENT：让 DWM 渲染完整内容（含子控件），Win8.1+
            if (!NativeMethods.PrintWindow(form.Handle, hdc, NativeMethods.PwRenderFullContent))
            {
                bmp.Dispose();
                return null;
            }
        }
        catch
        {
            bmp.Dispose();
            return null;
        }
        finally
        {
            g.ReleaseHdc(hdc);
        }
        return bmp;
    }

    /// <summary>回退方案：DrawToBitmap（走 WM_PRINT，不依赖窗口可见性）。</summary>
    private static Bitmap CaptureViaDrawToBitmap(Form form)
    {
        var bmp = new Bitmap(Math.Max(1, form.Width), Math.Max(1, form.Height));
        form.DrawToBitmap(bmp, new Rectangle(0, 0, bmp.Width, bmp.Height));
        return bmp;
    }

    /// <summary>
    /// 断言截图确实是一张窗体图像，而不是别的东西。
    ///
    /// 两道检查互补：颜色数排除"纯色空白"；浅色占比排除"抓到了深色控制台/桌面"。
    /// 本窗体以浅色为主（默认背景 ~240、日志区 ~250），实测正常截图浅色占比远超 0.35，
    /// 而抓错窗口时只有 0.1 左右。
    /// </summary>
    private static void AssertLooksLikeForm(Bitmap bmp, string name)
    {
        Assert.True(bmp.Width > 100 && bmp.Height > 100,
            $"截图尺寸异常（{name}）: {bmp.Width}x{bmp.Height}");

        var colors = CountDistinctColors(bmp);
        Assert.True(colors >= 40,
            $"截图疑似空白（{name}）: 仅 {colors} 种颜色");

        var lightRatio = LightPixelRatio(bmp);
        Assert.True(lightRatio >= 0.35,
            $"截图不像本窗体（{name}）: 浅色像素仅 {lightRatio:P0}，" +
            "疑似抓到了被其它窗口覆盖的屏幕内容");
    }

    /// <summary>浅色像素占比（判定截图是否为浅色窗体）。</summary>
    public static double LightPixelRatio(Bitmap bmp, int threshold = 200)
    {
        var light = 0;
        var total = 0;
        const int step = 4;
        for (var y = 0; y < bmp.Height; y += step)
        {
            for (var x = 0; x < bmp.Width; x += step)
            {
                var c = bmp.GetPixel(x, y);
                total++;
                if (c.R >= threshold && c.G >= threshold && c.B >= threshold) light++;
            }
        }
        return total == 0 ? 0 : (double)light / total;
    }

    /// <summary>内容指纹，用于断言不同状态的截图确实不同。</summary>
    public static string Fingerprint(string pngPath)
    {
        using var stream = File.OpenRead(pngPath);
        return Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(stream));
    }

    /// <summary>
    /// 复选框/单选按钮左侧字形占用的宽度。
    ///
    /// 这类控件的 Text 渲染在字形右侧，因此可用宽度 = 控件宽度 − 字形宽度 − 内边距。
    /// 忽略它就会得出"86px 放得下 72px 文字"的错误结论，
    /// 而实际在 150% 缩放下末字会被裁掉。
    /// 用 SystemInformation 取真实字形尺寸，而非硬编码常数。
    /// </summary>
    public static int GlyphWidth(Control control) => control switch
    {
        CheckBox => SystemInformation.MenuCheckSize.Width + 4,
        RadioButton => SystemInformation.MenuCheckSize.Width + 4,
        _ => 0,
    };

    private static string Save(Bitmap bmp, string fileName)
    {
        var path = Path.Combine(ScreenshotDir, fileName);
        bmp.Save(path, ImageFormat.Png);
        return path;
    }

    /// <summary>
    /// 统计不同颜色数，用于判定截图确实渲染出了内容而不是一块纯色。
    /// 空窗体/未渲染的窗体颜色数极少，这条断言能挡住"截了个寂寞"的假通过。
    /// </summary>
    public static int CountDistinctColors(Bitmap bmp)
    {
        var seen = new HashSet<int>();
        // 抽样步长：足够发现内容，又不至于逐像素跑满
        const int step = 3;
        for (var y = 0; y < bmp.Height; y += step)
        {
            for (var x = 0; x < bmp.Width; x += step)
            {
                seen.Add(bmp.GetPixel(x, y).ToArgb());
                if (seen.Count > 5000) return seen.Count;
            }
        }
        return seen.Count;
    }

    /// <summary>
    /// 生成一个自包含的样本视频（含音轨），避免测试依赖外部素材路径。
    /// ffmpeg 不可用时返回 null，调用方应跳过相关用例。
    /// </summary>
    public static string? TryCreateSampleVideo(int seconds = 3, int width = 640, int height = 360)
    {
        var ffmpeg = MediaCli.Transcode.Bin.FfmpegBin.ResolveFFmpegBinary();
        if (string.IsNullOrEmpty(ffmpeg)) return null;

        var dir = Path.Combine(Path.GetTempPath(), "ffconvnet-gui-tests");
        Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, $"sample-{width}x{height}-{seconds}s.mp4");
        if (File.Exists(path) && new FileInfo(path).Length > 0) return path;

        var psi = new ProcessStartInfo
        {
            FileName = ffmpeg,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        foreach (var a in new[]
                 {
                     "-hide_banner", "-v", "error",
                     "-f", "lavfi", "-i", $"testsrc=size={width}x{height}:rate=30:duration={seconds}",
                     "-f", "lavfi", "-i", $"sine=frequency=440:duration={seconds}",
                     "-c:v", "libx264", "-preset", "ultrafast", "-pix_fmt", "yuv420p",
                     "-c:a", "aac", "-b:a", "128k",
                     "-y", path,
                 })
        {
            psi.ArgumentList.Add(a);
        }

        using var proc = Process.Start(psi);
        if (proc is null) return null;
        proc.StandardError.ReadToEnd();
        proc.WaitForExit(180_000);
        return File.Exists(path) && new FileInfo(path).Length > 0 ? path : null;
    }

    /// <summary>轮询等待条件成立（同时泵消息，保证 UI 有机会更新）。</summary>
    public static bool WaitUntil(Func<bool> condition, int timeoutMs = 30_000)
    {
        var sw = Stopwatch.StartNew();
        while (sw.ElapsedMilliseconds < timeoutMs)
        {
            if (condition()) return true;
            Pump(50);
        }
        return condition();
    }

    /// <summary>遍历控件树（父 → 子）。</summary>
    public static IEnumerable<(Control Child, Control Parent)> Walk(Control root)
    {
        foreach (Control c in root.Controls)
        {
            yield return (c, root);
            foreach (var pair in Walk(c)) yield return pair;
        }
    }

    /// <summary>Win32 互操作（仅测试用，用于可靠截图）。</summary>
    internal static class NativeMethods
    {
        /// <summary>PW_RENDERFULLCONTENT（Win 8.1+）：让 DWM 渲染含子控件的完整内容。</summary>
        public const uint PwRenderFullContent = 0x00000002;

        [System.Runtime.InteropServices.DllImport("user32.dll", SetLastError = true)]
        [return: System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.Bool)]
        public static extern bool PrintWindow(IntPtr hwnd, IntPtr hdcBlt, uint nFlags);

        [System.Runtime.InteropServices.DllImport("user32.dll")]
        public static extern uint GetDpiForSystem();
    }
}
