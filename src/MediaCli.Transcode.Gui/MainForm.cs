using System.Diagnostics;
using MediaCli.Transcode.Bin;
using MediaCli.Transcode.Hardware;
using MediaCli.Transcode.Run;

namespace MediaCli.Transcode.Gui;

/// <summary>
/// 主窗体（纯代码布局，无设计器文件）。
///
/// 职责边界：只做 UI 与交互；转码语义全部委托给 core，
/// 选项映射在 <see cref="GuiOptions"/>，编排在 <see cref="TranscodeSession"/>。
/// </summary>
public class MainForm : Form
{
    // ---- 输入 ----
    // 每个可交互控件都带 Name：既是可访问性标识，也是 UI 测试的查找键
    // （测试通过 Controls.Find(name, searchAllChildren: true) 定位，不依赖坐标）。
    private readonly TextBox inputBox = new() { Name = "inputBox" };
    private readonly Button btnFile = new() { Name = "btnFile" };
    private readonly Button btnDir = new() { Name = "btnDir" };

    // ---- 参数 ----
    private readonly ComboBox presetCombo = new() { Name = "presetCombo" };
    private readonly ComboBox hwaccelCombo = new() { Name = "hwaccelCombo" };
    private readonly ComboBox decodeCombo = new() { Name = "decodeCombo" };
    private readonly TextBox outputBox = new() { Name = "outputBox" };
    private readonly Button btnOutput = new() { Name = "btnOutput" };
    private readonly RadioButton modeDir = new() { Name = "modeDir" };
    private readonly RadioButton modeTree = new() { Name = "modeTree" };
    private readonly RadioButton modeFile = new() { Name = "modeFile" };
    private readonly TextBox ffargsBox = new() { Name = "ffargsBox" };
    private readonly CheckBox overrideCheck = new() { Name = "overrideCheck" };
    private readonly CheckBox strictCheck = new() { Name = "strictCheck" };
    private readonly CheckBox debugCheck = new() { Name = "debugCheck" };
    private readonly CheckBox animeCheck = new() { Name = "animeCheck" };

    // ---- 操作 ----
    private readonly Button btnPreview = new() { Name = "btnPreview" };
    private readonly Button btnRun = new() { Name = "btnRun" };
    private readonly Button btnCancel = new() { Name = "btnCancel" };
    private readonly Button btnClear = new() { Name = "btnClear" };
    private readonly Button btnOpenOutput = new() { Name = "btnOpenOutput" };

    // ---- 日志与进度 ----
    private readonly RichTextBox logBox = new() { Name = "logBox" };
    private readonly ProgressBar progressBar = new() { Name = "progressBar" };
    private readonly Label statusLabel = new() { Name = "statusLabel" };
    private readonly System.Windows.Forms.Timer flushTimer = new();

    private readonly LogSink sink = new();
    private CancellationTokenSource? cts;
    private Task<SessionSummary>? running;
    private volatile int lastPercent;
    private volatile string lastSpeed = "";
    private volatile bool progressDirty;
    private string? lastOutputDir;

    /// <summary>当前是否有任务在跑（供 UI 测试与外部状态展示）。</summary>
    public bool IsRunning => running is not null;

    public MainForm()
    {
        Text = "mediac GUI — FFmpeg 转码（MediaCli.Transcode core）";
        ClientSize = new Size(980, 760);
        MinimumSize = new Size(860, 620);
        StartPosition = FormStartPosition.CenterScreen;
        Font = new Font("Microsoft YaHei UI", 9F);

        BuildInputGroup();
        BuildParamGroup();
        BuildActionGroup();
        BuildLogGroup();

        flushTimer.Interval = 100;
        flushTimer.Tick += (_, _) => Flush();
        flushTimer.Start();

        FormClosing += OnFormClosing;
        Shown += (_, _) => StartupDiagnostics();

        // 缩放基准必须最后设置。
        //
        // 纯代码创建的 Form 默认 AutoScaleMode.Inherit 且 AutoScaleDimensions=(0,0)，
        // 即"完全不缩放"（设计器生成的窗体都会显式设，代码写窗体时极易漏掉）。
        // 后果：在 >96 DPI 的屏幕上字体按缩放比放大（实测 150%），控件却仍是代码里的
        // 像素尺寸，固定布局被挤爆——按钮、复选框文字被裁掉。
        //
        // 顺序很关键：Font 的 setter 会重算 AutoScaleDimensions，所以必须在设置 Font
        // 与全部布局之后再落基准；否则基准被覆盖为当前 DPI，比值变 1.0 而依旧不缩放。
        AutoScaleMode = AutoScaleMode.Dpi;
        AutoScaleDimensions = new SizeF(96F, 96F);   // 布局按 96 DPI 设计
    }

    // ==================================================================
    // 布局
    // ==================================================================

    private void BuildInputGroup()
    {
        var box = new GroupBox
        {
            Text = "输入",
            Location = new Point(12, 12),
            Size = new Size(956, 104),
            Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right,
        };

        btnFile.Text = "选择文件…";
        btnFile.SetBounds(14, 22, 120, 26);
        btnFile.Click += OnPickFiles;

        btnDir.Text = "选择目录…";
        btnDir.SetBounds(142, 22, 120, 26);
        btnDir.Click += OnPickFolder;

        var hint = new Label
        {
            Name = "inputHint",
            Text = "每行一个路径；目录会被递归扫描。也可直接粘贴。",
            ForeColor = Color.Gray,
            AutoSize = true,
            Location = new Point(274, 28),
        };

        inputBox.Multiline = true;
        inputBox.ScrollBars = ScrollBars.Vertical;
        inputBox.SetBounds(14, 54, 928, 40);
        inputBox.Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right;
        inputBox.TextChanged += (_, _) => RefreshInputCount();

        box.Controls.AddRange([btnFile, btnDir, hint, inputBox]);
        Controls.Add(box);
    }

    private void BuildParamGroup()
    {
        // 高度按内容精确排布，避免控件互相压盖或贴到边框：
        //   28  预设/hwaccel/解码模式
        //   66  输出目录
        //   104 输出模式
        //   136 自定义参数
        //   160/176 两行说明
        //   196 复选框行
        var box = new GroupBox
        {
            Text = "参数",
            Location = new Point(12, 124),
            Size = new Size(956, 236),
            Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right,
        };

        var lblPreset = new Label { Text = "Preset", AutoSize = true, Location = new Point(14, 32) };
        presetCombo.SetBounds(62, 28, 170, 23);
        presetCombo.DropDownStyle = ComboBoxStyle.DropDownList;

        var lblHw = new Label { Text = "hwaccel", AutoSize = true, Location = new Point(248, 32) };
        hwaccelCombo.SetBounds(306, 28, 130, 23);
        hwaccelCombo.DropDownStyle = ComboBoxStyle.DropDownList;
        hwaccelCombo.Items.AddRange(["auto", "cuda", "qsv", "amf", "d3d", "d3d11va", "d3d12va", "dxva2", "cpu"]);
        hwaccelCombo.SelectedIndex = 0;

        var lblDec = new Label { Text = "解码模式", AutoSize = true, Location = new Point(452, 32) };
        decodeCombo.SetBounds(516, 28, 110, 23);
        decodeCombo.DropDownStyle = ComboBoxStyle.DropDownList;
        decodeCombo.Items.AddRange(["auto", "gpu", "cpu"]);
        decodeCombo.SelectedIndex = 0;

        var lblOut = new Label { Text = "输出目录", AutoSize = true, Location = new Point(14, 70) };
        outputBox.SetBounds(78, 66, 760, 23);
        outputBox.Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right;
        btnOutput.Text = "浏览…";
        btnOutput.SetBounds(846, 65, 100, 24);
        btnOutput.Anchor = AnchorStyles.Top | AnchorStyles.Right;
        btnOutput.Click += OnPickOutput;

        var lblMode = new Label { Text = "输出模式", AutoSize = true, Location = new Point(14, 106) };
        modeDir.Text = "dir";
        modeDir.SetBounds(80, 104, 60, 20);
        modeDir.Checked = true;
        modeTree.Text = "tree";
        modeTree.SetBounds(146, 104, 66, 20);
        modeFile.Text = "file";
        modeFile.SetBounds(218, 104, 60, 20);

        var lblArgs = new Label { Text = "自定义参数", AutoSize = true, Location = new Point(14, 140) };
        ffargsBox.SetBounds(90, 136, 856, 23);
        ffargsBox.Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right;
        ffargsBox.PlaceholderText = "例如 vb=3000000,vq=23,sp=1.5（码率写裸 bps）";

        // 说明文字拆成两行：单行放不下会在分组框右缘被硬截断（实测 1037px > 最小窗口的 794px）。
        // 两行都必须与上下相邻控件留出间距，见 MainFormLayoutTests.SingleLineText_IsNotClipped。
        var ffHint = new Label
        {
            Name = "ffargsHint",
            Text = "别名：vb码率 vq质量 vc编码器 ab音频码率 aq质量 ac编码器 sp速度 dm尺寸 fps帧率 md元数据",
            ForeColor = Color.Gray,
            AutoSize = false,
            Location = new Point(14, 161),
            Size = new Size(930, 16),
            Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right,
        };

        var ffHint2 = new Label
        {
            Name = "ffargsHint2",
            Text = "码率写裸 bps（如 vb=3000000，带单位会被丢弃）；强制软解选「解码模式 = cpu」",
            ForeColor = Color.Gray,
            AutoSize = false,
            Location = new Point(14, 177),
            Size = new Size(930, 16),
            Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right,
        };

        overrideCheck.Text = "覆盖已有";
        overrideCheck.SetBounds(16, 198, 86, 20);
        strictCheck.Text = "严格模式";
        strictCheck.SetBounds(110, 198, 86, 20);
        debugCheck.Text = "详细日志";
        debugCheck.SetBounds(204, 198, 86, 20);
        animeCheck.Text = "动漫模式";
        animeCheck.SetBounds(298, 198, 86, 20);

        box.Controls.AddRange([
            lblPreset, presetCombo, lblHw, hwaccelCombo, lblDec, decodeCombo,
            lblOut, outputBox, btnOutput, lblMode, modeDir, modeTree, modeFile,
            lblArgs, ffargsBox, ffHint, ffHint2, overrideCheck, strictCheck, debugCheck, animeCheck,
        ]);
        Controls.Add(box);
    }

    private void BuildActionGroup()
    {
        var box = new GroupBox
        {
            Text = "操作",
            Location = new Point(12, 368),
            Size = new Size(956, 64),
            Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right,
        };

        btnPreview.Text = "预览命令";
        btnPreview.SetBounds(14, 24, 110, 28);
        btnPreview.Click += (_, _) => StartRun(doit: false);

        btnRun.Text = "开始转码";
        btnRun.SetBounds(132, 24, 110, 28);
        btnRun.Click += (_, _) => StartRun(doit: true);

        btnCancel.Text = "取消";
        btnCancel.SetBounds(250, 24, 90, 28);
        btnCancel.Enabled = false;
        btnCancel.Click += OnCancel;

        btnClear.Text = "清空日志";
        btnClear.SetBounds(348, 24, 90, 28);
        btnClear.Click += OnClearLog;

        btnOpenOutput.Text = "打开输出目录";
        btnOpenOutput.SetBounds(446, 24, 120, 28);
        btnOpenOutput.Click += OnOpenOutput;

        box.Controls.AddRange([btnPreview, btnRun, btnCancel, btnClear, btnOpenOutput]);
        Controls.Add(box);
    }

    private void BuildLogGroup()
    {
        var box = new GroupBox
        {
            Text = "日志",
            Location = new Point(12, 440),
            Size = new Size(956, 308),
            Anchor = AnchorStyles.Top | AnchorStyles.Bottom | AnchorStyles.Left | AnchorStyles.Right,
        };

        logBox.SetBounds(14, 24, 928, 242);
        logBox.Anchor = AnchorStyles.Top | AnchorStyles.Bottom | AnchorStyles.Left | AnchorStyles.Right;
        logBox.ReadOnly = true;
        logBox.BackColor = Color.FromArgb(250, 250, 250);
        logBox.Font = new Font("Consolas", 9F);
        logBox.WordWrap = false;
        logBox.ScrollBars = RichTextBoxScrollBars.Both;
        logBox.DetectUrls = false;

        progressBar.SetBounds(14, 274, 720, 18);
        progressBar.Anchor = AnchorStyles.Bottom | AnchorStyles.Left | AnchorStyles.Right;
        progressBar.Minimum = 0;
        progressBar.Maximum = 100;

        statusLabel.Text = "就绪";
        statusLabel.SetBounds(744, 276, 198, 18);
        statusLabel.Anchor = AnchorStyles.Bottom | AnchorStyles.Right;
        statusLabel.TextAlign = ContentAlignment.MiddleRight;

        box.Controls.AddRange([logBox, progressBar, statusLabel]);
        Controls.Add(box);
    }

    // ==================================================================
    // 模态交互钩子（可测试性接缝）
    // ==================================================================

    /// <summary>
    /// 确认类对话框（有取消语义）。默认弹 MessageBox。
    ///
    /// 声明为 <c>virtual</c> 是为了让 UI 测试能覆写它：模态框会阻塞调用线程，
    /// 无人值守的自动化一旦撞上就会永久挂起。生产路径行为不变。
    /// </summary>
    protected virtual bool Confirm(string message, string title) =>
        MessageBox.Show(this, message, title, MessageBoxButtons.OKCancel, MessageBoxIcon.Warning)
        == DialogResult.OK;

    /// <summary>提示类对话框（无取消语义）。同上，供 UI 测试覆写。</summary>
    protected virtual void Notify(string message, string title) =>
        MessageBox.Show(this, message, title, MessageBoxButtons.OK, MessageBoxIcon.Information);

    // ==================================================================
    // 输入
    // ==================================================================

    private System.Windows.Forms.Timer? scanTimer;
    private bool droppedWarned;

    private void OnPickFiles(object? sender, EventArgs e)
    {
        using var dlg = new OpenFileDialog
        {
            Title = "选择媒体文件",
            Multiselect = true,
            Filter = "媒体文件|*.mp4;*.mkv;*.mov;*.avi;*.webm;*.flv;*.ts;*.m2ts;*.wmv;*.mpg;*.mpeg;*.m4v;" +
                     "*.mp3;*.flac;*.wav;*.m4a;*.aac;*.opus;*.ogg;*.wma|所有文件|*.*",
        };
        if (dlg.ShowDialog(this) == DialogResult.OK) AddInputLines(dlg.FileNames);
    }

    private void OnPickFolder(object? sender, EventArgs e)
    {
        using var dlg = new FolderBrowserDialog { Description = "选择目录（会递归扫描媒体文件）" };
        if (dlg.ShowDialog(this) == DialogResult.OK) AddInputLines([dlg.SelectedPath]);
    }

    private void AddInputLines(IEnumerable<string> paths)
    {
        var list = InputLines();
        foreach (var p in paths)
        {
            if (!list.Contains(p, StringComparer.OrdinalIgnoreCase)) list.Add(p);
        }
        inputBox.Text = string.Join(Environment.NewLine, list);
    }

    private List<string> InputLines() =>
        inputBox.Lines.Select(l => l.Trim().Trim('"')).Where(l => l.Length > 0).ToList();

    /// <summary>输入变化后延迟 600ms 再扫描，避免逐字符递归枚举目录。</summary>
    private void RefreshInputCount()
    {
        scanTimer ??= CreateScanTimer();
        scanTimer.Stop();
        scanTimer.Start();
    }

    private System.Windows.Forms.Timer CreateScanTimer()
    {
        var t = new System.Windows.Forms.Timer { Interval = 600 };
        t.Tick += (_, _) => { t.Stop(); ScanInputs(); };
        return t;
    }

    private void ScanInputs()
    {
        var paths = InputLines();
        if (paths.Count == 0) { statusLabel.Text = "就绪"; return; }
        statusLabel.Text = "正在扫描输入…";
        var opts = ReadOptions();
        opts.Inputs.Clear();
        opts.Inputs.AddRange(paths);
        Task.Run(() =>
        {
            string msg;
            try { msg = $"已收集 {TranscodeSession.CollectFiles(opts).Count} 个文件"; }
            catch (Exception ex) { msg = $"扫描失败：{ex.Message}"; }
            SafeUi(() => statusLabel.Text = msg);
        });
    }

    private void OnPickOutput(object? sender, EventArgs e)
    {
        using var dlg = new FolderBrowserDialog { Description = "选择输出目录" };
        if (dlg.ShowDialog(this) == DialogResult.OK) outputBox.Text = dlg.SelectedPath;
    }

    private GuiOptions ReadOptions() => new()
    {
        Inputs = InputLines(),
        Preset = presetCombo.SelectedItem as string ?? "hevc_2k",
        Output = outputBox.Text.Trim(),
        OutputMode = modeTree.Checked ? "tree" : modeFile.Checked ? "file" : "dir",
        Hwaccel = hwaccelCombo.SelectedItem as string ?? "auto",
        DecodeMode = decodeCombo.SelectedItem as string ?? "auto",
        Ffargs = ffargsBox.Text.Trim(),
        Override = overrideCheck.Checked,
        Strict = strictCheck.Checked,
        Debug = debugCheck.Checked,
        Anime = animeCheck.Checked,
    };

    // ==================================================================
    // 运行控制
    // ==================================================================

    private void StartRun(bool doit)
    {
        if (running is not null) return;
        var opts = ReadOptions();
        if (opts.Inputs.Count == 0)
        {
            Notify("请先选择输入文件或目录。", "缺少输入");
            return;
        }

        // ffargs 校验结果必须在开跑前摆到台面上。
        // 若只交给 core 的 applyFfargs，`vb=3M` 这类写法会被**静默丢弃**——
        // 用户会以为参数生效了，实际没有。这正是 FfargsValidator 存在的意义。
        var ffargsResult = opts.ValidateFfargs();
        if (ffargsResult.HasWarnings)
        {
            var detail = string.Join(Environment.NewLine + "  · ", ffargsResult.Warnings);
            if (!Confirm(
                "自定义参数存在不会生效的写法：" + Environment.NewLine + Environment.NewLine +
                "  · " + detail + Environment.NewLine + Environment.NewLine +
                "仍要继续吗？（这些项将被忽略，其余参数正常应用）",
                "自定义参数提示")) return;
        }

        if (doit && !overrideCheck.Checked)
        {
            if (!Confirm(
                "未勾选「覆盖已有」：目标文件已存在时会自动跳过（core 契约）。继续执行？",
                "确认执行")) return;
        }

        sink.Clear();
        logBox.Clear();
        progressBar.Value = 0;
        lastPercent = 0;
        lastSpeed = "";
        progressDirty = false;
        droppedWarned = false;
        lastOutputDir = string.IsNullOrWhiteSpace(opts.Output) ? null : opts.Output;
        SetBusy(true);

        AppendLog(SessionLogLevel.Info, doit ? "=== 开始转码 ===" : "=== 预览命令（不写盘）===");
        foreach (var w in ffargsResult.Warnings) AppendLog(SessionLogLevel.Warn, w);
        cts = new CancellationTokenSource();
        var token = cts.Token;

        // core 的执行是同步的（内部 WaitForExit），必须整体放到后台线程，否则 UI 冻死。
        running = Task.Run(() =>
        {
            var session = new TranscodeSession(
                (lvl, text) => sink.Write(lvl, text),
                p =>
                {
                    lastPercent = (int)Math.Round(p.Percent);
                    lastSpeed = p.Speed;
                    progressDirty = true;
                });
            try
            {
                return session.Run(opts, doit, token);
            }
            catch (Exception ex)
            {
                sink.Write(SessionLogLevel.Error, $"未处理异常：{ex.Message}");
                return new SessionSummary();
            }
        });

        running.ContinueWith(t => SafeUi(() => FinishRun(t)), TaskScheduler.Default);
    }

    private void FinishRun(Task<SessionSummary> task)
    {
        running = null;
        try { cts?.Dispose(); } catch { /* ignore */ }
        cts = null;
        SetBusy(false);

        if (task.IsFaulted)
        {
            AppendLog(SessionLogLevel.Error,
                $"任务异常：{task.Exception?.GetBaseException().Message}");
            statusLabel.Text = "已结束（异常）";
            return;
        }
        if (!task.IsCompletedSuccessfully)
        {
            AppendLog(SessionLogLevel.Warn, "任务已取消。");
            progressBar.Value = 0;
            statusLabel.Text = "已取消";
            return;
        }

        var s = task.Result;
        if (s.Total == 0)
        {
            statusLabel.Text = "无处理项";
            return;
        }
        progressBar.Value = 100;
        var parts = new List<string>();
        if (s.Preview > 0) parts.Add($"预览 {s.Preview}");
        if (s.Success > 0) parts.Add($"成功 {s.Success}");
        if (s.Failed > 0) parts.Add($"失败 {s.Failed}");
        if (s.Skipped > 0) parts.Add($"跳过 {s.Skipped}");
        if (s.Cancelled > 0) parts.Add($"取消 {s.Cancelled}");
        statusLabel.Text = s.WasCancelled ? "已取消" : string.Join(" / ", parts);
    }

    private void OnCancel(object? sender, EventArgs e)
    {
        if (cts is null || cts.IsCancellationRequested) return;
        AppendLog(SessionLogLevel.Warn,
            "正在取消…（首次硬件探测期间取消会延迟生效：探测子进程不接收取消信号）");
        btnCancel.Enabled = false;
        try { cts.Cancel(); } catch (ObjectDisposedException) { /* ignore */ }
    }

    private void SetBusy(bool busy)
    {
        btnRun.Enabled = !busy;
        btnPreview.Enabled = !busy;
        btnCancel.Enabled = busy;
        presetCombo.Enabled = !busy;
        hwaccelCombo.Enabled = !busy;
        decodeCombo.Enabled = !busy;
        inputBox.ReadOnly = busy;
    }

    // ==================================================================
    // 日志刷新与着色
    // ==================================================================

    /// <summary>
    /// UI 线程定时器回调：批量 drain 队列并着色。
    /// 生产者（core 的回调线程）只入队不等待，因此不会拖慢转码。
    /// </summary>
    private void Flush()
    {
        if (progressDirty)
        {
            progressDirty = false;
            var v = Math.Clamp(lastPercent, progressBar.Minimum, progressBar.Maximum);
            if (v != progressBar.Value) progressBar.Value = v;
            if (running is not null)
            {
                statusLabel.Text = $"{v}%" + (string.IsNullOrEmpty(lastSpeed) ? "" : $"   {lastSpeed}");
            }
        }

        var batch = sink.Drain();
        if (batch.Count == 0)
        {
            WarnIfDropped();
            return;
        }

        logBox.SuspendLayout();
        try
        {
            foreach (var line in batch) AppendLogRaw(line);
            TrimLogBox();
        }
        finally
        {
            logBox.ResumeLayout();
            if (logBox.Visible) logBox.SelectionStart = logBox.TextLength;
            if (logBox.Visible) logBox.ScrollToCaret();
        }
        WarnIfDropped();
    }

    private void WarnIfDropped()
    {
        var dropped = sink.DroppedCount;
        if (dropped == 0 || droppedWarned) return;
        droppedWarned = true;
        AppendLogRaw(new LogLine(SessionLogLevel.Warn,
            $"日志量过大，已丢弃 {dropped} 条（显示上限 {LogSink.DisplayLimit} 行）。" +
            "可关闭「详细日志」减少输出。", DateTime.Now));
    }

    private void AppendLogRaw(LogLine line)
    {
        logBox.SelectionStart = logBox.TextLength;
        logBox.SelectionLength = 0;
        logBox.SelectionColor = ColorFor(line.Level);
        logBox.AppendText($"[{line.At:HH:mm:ss}] {Prefix(line.Level)}{line.Text}{Environment.NewLine}");
        logBox.SelectionColor = logBox.ForeColor;
    }

    /// <summary>UI 线程直接写日志（用于 UI 自身产生的事件）。</summary>
    private void AppendLog(SessionLogLevel level, string text) =>
        sink.Write(level, text);

    private static string Prefix(SessionLogLevel level) => level switch
    {
        SessionLogLevel.Cmd => "",
        SessionLogLevel.Warn => "! ",
        SessionLogLevel.Error => "× ",
        SessionLogLevel.Done => "✓ ",
        _ => "  ",
    };

    private static Color ColorFor(SessionLogLevel level) => level switch
    {
        SessionLogLevel.Cmd => Color.FromArgb(0x0B, 0x53, 0x94),
        SessionLogLevel.Warn => Color.FromArgb(0xA6, 0x6A, 0x00),
        SessionLogLevel.Error => Color.FromArgb(0xB0, 0x1E, 0x1E),
        SessionLogLevel.Done => Color.FromArgb(0x1B, 0x6B, 0x2F),
        _ => Color.FromArgb(0x30, 0x30, 0x30),
    };

    /// <summary>超出上限时裁掉最前面的行，避免 RichTextBox 内存无限增长。</summary>
    private void TrimLogBox()
    {
        if (logBox.Lines.Length <= LogSink.DisplayLimit) return;
        var excess = logBox.Lines.Length - LogSink.DisplayLimit;
        var firstKeep = logBox.GetFirstCharIndexFromLine(excess);
        if (firstKeep <= 0) return;
        logBox.Select(0, firstKeep);
        logBox.SelectedText = string.Empty;
    }

    private void OnClearLog(object? sender, EventArgs e)
    {
        sink.Clear();
        logBox.Clear();
        droppedWarned = false;
        progressBar.Value = 0;
        statusLabel.Text = running is null ? "就绪" : statusLabel.Text;
    }

    private void OnOpenOutput(object? sender, EventArgs e)
    {
        var dir = lastOutputDir;
        if (string.IsNullOrEmpty(dir) || !Directory.Exists(dir))
        {
            var first = ReadOptions().Inputs.FirstOrDefault();
            if (first is not null)
            {
                dir = File.Exists(first) ? Path.GetDirectoryName(first) : first;
            }
        }
        if (string.IsNullOrEmpty(dir) || !Directory.Exists(dir))
        {
            Notify("输出目录尚未确定或不存在。", "无法打开");
            return;
        }
        try
        {
            Process.Start(new ProcessStartInfo { FileName = dir, UseShellExecute = true });
        }
        catch (Exception ex)
        {
            Notify($"打开目录失败：{ex.Message}", "错误");
        }
    }

    // ==================================================================
    // 启动诊断
    // ==================================================================

    /// <summary>
    /// 首屏告诉用户「实际在用哪个 ffmpeg、走的哪一层」。
    /// 用户永远能自证环境，而不是遇到问题时无从下手。
    /// </summary>
    private void StartupDiagnostics()
    {
        // 预设下拉：先填上再打印，避免用户以为界面没加载
        try
        {
            var names = TranscodeSession.PresetNames();
            presetCombo.Items.Clear();
            presetCombo.Items.AddRange(names.Cast<object>().ToArray());
            var idx = presetCombo.Items.IndexOf("hevc_2k");
            presetCombo.SelectedIndex = idx >= 0 ? idx : 0;
            AppendLog(SessionLogLevel.Info, $"已加载 {names.Count} 个预设。");
        }
        catch (Exception ex)
        {
            AppendLog(SessionLogLevel.Error, $"预设加载失败：{ex.Message}");
        }

        var ffmpeg = FfmpegBin.ResolveFFmpegBinary();
        if (string.IsNullOrEmpty(ffmpeg))
        {
            AppendLog(SessionLogLevel.Error,
                "未找到 ffmpeg。请设置 FFMPEG_PATH 环境变量，或将其加入 PATH。");
            btnRun.Enabled = false;
            btnPreview.Enabled = false;
            statusLabel.Text = "缺少 ffmpeg";
            return;
        }
        FfmpegRun.SetFFmpegPath(ffmpeg);
        AppendLog(SessionLogLevel.Info, $"ffmpeg: {ffmpeg}");

        var ffprobe = FfmpegBin.ResolveFFprobeBinary(ffmpeg);
        AppendLog(SessionLogLevel.Info,
            ffprobe is null ? "ffprobe: 未找到（媒体信息探测会失败）" : $"ffprobe: {ffprobe}");

        // 硬件能力探测放到后台，避免首屏卡住；探测结果会被 core 缓存复用
        Task.Run(() =>
        {
            try
            {
                var caps = HwDetect.DetectHardwareCapabilities(ffmpeg);
                var gpus = caps.Gpus.Count > 0
                    ? string.Join(", ", caps.Gpus.Select(g => g.Model))
                    : "未识别";
                var usable = caps.Usable.Where(kv => kv.Value).Select(kv => kv.Key).ToList();
                var msg = $"版本 {caps.Version}  ·  厂商 {caps.Vendor}  ·  GPU {gpus}  ·  " +
                          $"可用硬件层 {(usable.Count > 0 ? string.Join("/", usable) : "无（将走 cpu）")}";
                sink.Write(SessionLogLevel.Info, msg);
            }
            catch (Exception ex)
            {
                sink.Write(SessionLogLevel.Warn, $"硬件能力探测失败（不影响 cpu 层转码）：{ex.Message}");
            }
        });
    }

    // ==================================================================
    // 收尾
    // ==================================================================

    /// <summary>
    /// 关窗保护：任务在跑时必须先取消并等进程退出，
    /// 否则 ffmpeg 会变成孤儿进程继续占用 GPU。
    /// </summary>
    private void OnFormClosing(object? sender, FormClosingEventArgs e)
    {
        if (running is null) return;
        if (!Confirm(
            "转码任务仍在运行。关闭窗口会终止它，是否继续？",
            "确认关闭"))
        {
            e.Cancel = true;
            return;
        }

        try { cts?.Cancel(); } catch (ObjectDisposedException) { /* ignore */ }
        try
        {
            // 给 core 的 kill 链路与临时文件清理留出时间；超时则不再等待，避免卡死界面
            running.Wait(TimeSpan.FromSeconds(10));
        }
        catch
        {
            // 忽略：等待超时或任务异常都不应阻止关窗
        }
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            flushTimer.Stop();
            flushTimer.Dispose();
            scanTimer?.Stop();
            scanTimer?.Dispose();
            try { cts?.Cancel(); } catch { /* ignore */ }
            try { cts?.Dispose(); } catch { /* ignore */ }
        }
        base.Dispose(disposing);
    }

    /// <summary>把动作安全地投递到 UI 线程（句柄未创建时忽略，避免关闭竞态抛错）。</summary>
    private void SafeUi(Action action)
    {
        try
        {
            if (IsDisposed || Disposing || !IsHandleCreated) return;
            if (InvokeRequired) BeginInvoke(action);
            else action();
        }
        catch (ObjectDisposedException) { /* ignore */ }
        catch (InvalidOperationException) { /* ignore: 句柄销毁竞态 */ }
    }
}

