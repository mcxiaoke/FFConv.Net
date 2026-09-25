using System.Diagnostics;
using MediaCli.Transcode.Bin;
using MediaCli.Transcode.Hardware;
using MediaCli.Transcode.Run;
using MediaCli.Transcode.Support;

namespace MediaCli.Transcode.Gui;

/// <summary>
/// 主窗体（纯代码布局，无设计器文件）。
///
/// 职责边界：只做 UI 与交互；转码语义全部委托给 core，
/// 选项映射在 <see cref="GuiOptions"/>，编排在 <see cref="TranscodeSession"/>。
///
/// 界面顺序：输入 → 输出 → 参数 → 操作 → 日志，底部状态栏显示机器与工具链信息。
/// </summary>
public class MainForm : Form
{
    // ---- 输入 ----
    private readonly TextBox inputBox = new() { Name = "inputBox" };
    private readonly Button btnFile = new() { Name = "btnFile" };
    private readonly Button btnDir = new() { Name = "btnDir" };

    // ---- 输出 ----
    private readonly TextBox outputBox = new() { Name = "outputBox" };
    private readonly Button btnOutput = new() { Name = "btnOutput" };
    private readonly RadioButton modeDir = new() { Name = "modeDir" };
    private readonly RadioButton modeTree = new() { Name = "modeTree" };
    private readonly RadioButton modeFile = new() { Name = "modeFile" };

    // ---- 参数 ----
    private readonly ComboBox presetCombo = new() { Name = "presetCombo" };
    private readonly ComboBox hwaccelCombo = new() { Name = "hwaccelCombo" };
    private readonly ComboBox decodeCombo = new() { Name = "decodeCombo" };
    private readonly Button btnAbout = new() { Name = "btnAbout" };
    private readonly TextBox cliArgsBox = new() { Name = "cliArgsBox" };
    private readonly CheckBox overrideCheck = new() { Name = "overrideCheck" };
    private readonly CheckBox strictCheck = new() { Name = "strictCheck" };
    private readonly CheckBox debugCheck = new() { Name = "debugCheck" };
    private readonly CheckBox animeCheck = new() { Name = "animeCheck" };
    private readonly CheckBox syncLogCheck = new() { Name = "syncLogCheck" };

    // ---- 操作 ----
    private readonly Button btnPreview = new() { Name = "btnPreview" };
    private readonly Button btnRun = new() { Name = "btnRun" };
    private readonly Button btnCancel = new() { Name = "btnCancel" };
    private readonly Button btnClear = new() { Name = "btnClear" };
    private readonly Button btnOpenOutput = new() { Name = "btnOpenOutput" };

    // ---- 日志与进度 ----
    private readonly RichTextBox logBox = new() { Name = "logBox" };
    private readonly ProgressBar progressBar = new() { Name = "progressBar" };
    private readonly Label progressLabel = new() { Name = "progressLabel" };

    // ---- 状态栏 ----
    private readonly StatusStrip statusBar = new() { Name = "statusBar" };
    private readonly ToolStripStatusLabel systemInfoLabel = new() { Name = "systemInfoLabel" };
    private readonly ToolStripStatusLabel collectLabel = new() { Name = "collectLabel" };
    private readonly ToolStripStatusLabel stateLabel = new() { Name = "stateLabel" };

    private readonly LogSink sink = new();
    private readonly System.Windows.Forms.Timer flushTimer = new();
    private SessionLogWriter? logWriter;
    private CancellationTokenSource? cts;
    private Task<SessionSummary>? running;
    private System.Windows.Forms.Timer? scanTimer;
    private bool droppedWarned;
    private string? lastOutputDir;

    // 进度：文件级读数（进度条与进度文本都用它）
    private volatile int lastPercent;
    private volatile int lastFileIndex;
    private volatile int lastFileTotal;
    private volatile string lastFileName = "";
    private volatile bool progressDirty;

    /// <summary>当前是否有任务在跑（供 UI 测试与外部状态展示）。</summary>
    public bool IsRunning => running is not null;

    public MainForm()
    {
        Text = "mediac GUI — FFmpeg 转码（MediaCli.Transcode core）";
        ClientSize = new Size(1000, 800);
        MinimumSize = new Size(880, 640);
        StartPosition = FormStartPosition.CenterScreen;
        Font = new Font("Microsoft YaHei UI", 9F);

        BuildInputGroup();
        BuildOutputGroup();
        BuildParamGroup();
        BuildActionGroup();
        BuildLogGroup();
        BuildStatusBar();

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
        AutoScaleDimensions = new SizeF(96F, 96F);
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
            Size = new Size(976, 110),
            Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right,
        };

        btnFile.Text = "选择文件…";
        btnFile.SetBounds(14, 24, 110, 26);
        btnFile.Click += OnPickFiles;

        btnDir.Text = "选择目录…";
        btnDir.SetBounds(132, 24, 110, 26);
        btnDir.Click += OnPickFolder;

        var hint = new Label
        {
            Name = "inputHint",
            Text = "每行一个路径；目录会被递归扫描。",
            ForeColor = Color.Gray,
            AutoSize = true,
            Location = new Point(254, 30),
        };

        inputBox.Multiline = true;
        inputBox.ScrollBars = ScrollBars.Vertical;
        inputBox.SetBounds(14, 56, 948, 42);
        inputBox.Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right;
        inputBox.TextChanged += (_, _) => RefreshInputCount();

        box.Controls.AddRange([btnFile, btnDir, hint, inputBox]);
        Controls.Add(box);
    }

    private void BuildOutputGroup()
    {
        var box = new GroupBox
        {
            Text = "输出",
            Location = new Point(12, 130),
            Size = new Size(976, 92),
            Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right,
        };

        var lblOut = new Label { Name = "lblOutput", Text = "输出目录", AutoSize = true, Location = new Point(14, 32) };
        outputBox.SetBounds(80, 28, 782, 23);
        outputBox.Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right;

        btnOutput.Text = "浏览…";
        btnOutput.SetBounds(870, 27, 92, 24);
        btnOutput.Anchor = AnchorStyles.Top | AnchorStyles.Right;
        btnOutput.Click += OnPickOutput;

        var lblMode = new Label { Name = "lblMode", Text = "输出模式", AutoSize = true, Location = new Point(14, 64) };
        modeDir.Text = "dir";
        modeDir.SetBounds(80, 62, 58, 20);
        modeDir.Checked = true;
        modeTree.Text = "tree";
        modeTree.SetBounds(144, 62, 64, 20);
        modeFile.Text = "file";
        modeFile.SetBounds(214, 62, 58, 20);

        box.Controls.AddRange([lblOut, outputBox, btnOutput, lblMode, modeDir, modeTree, modeFile]);
        Controls.Add(box);
    }

    private void BuildParamGroup()
    {
        var box = new GroupBox
        {
            Text = "参数",
            Location = new Point(12, 230),
            Size = new Size(976, 158),
            Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right,
        };

        var lblPreset = new Label { Name = "lblPreset", Text = "Preset", AutoSize = true, Location = new Point(14, 34) };
        presetCombo.SetBounds(62, 30, 168, 23);
        presetCombo.DropDownStyle = ComboBoxStyle.DropDownList;

        var lblHw = new Label { Name = "lblHwaccel", Text = "hwaccel", AutoSize = true, Location = new Point(246, 34) };
        hwaccelCombo.SetBounds(304, 30, 118, 23);
        hwaccelCombo.DropDownStyle = ComboBoxStyle.DropDownList;
        hwaccelCombo.Items.AddRange(["auto", "cuda", "qsv", "amf", "d3d", "d3d11va", "d3d12va", "dxva2", "cpu"]);
        hwaccelCombo.SelectedIndex = 0;

        var lblDec = new Label { Name = "lblDecode", Text = "解码模式", AutoSize = true, Location = new Point(438, 34) };
        decodeCombo.SetBounds(502, 30, 96, 23);
        decodeCombo.DropDownStyle = ComboBoxStyle.DropDownList;
        decodeCombo.Items.AddRange(["auto", "gpu", "cpu"]);
        decodeCombo.SelectedIndex = 0;

        // 「使用说明」放在解码模式之后（需求指定位置）
        btnAbout.Text = "使用说明…";
        btnAbout.SetBounds(610, 29, 112, 25);
        btnAbout.Click += OnShowAbout;

        var lblArgs = new Label { Name = "lblArgs", Text = "自定义参数", AutoSize = true, Location = new Point(14, 70) };
        cliArgsBox.SetBounds(80, 66, 882, 23);
        cliArgsBox.Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right;
        cliArgsBox.PlaceholderText = "mediac CLI 写法，例：--video-bitrate 3M --video-quality 23";

        // 仅保留一行指引；详细说明移到「使用说明」窗口（需求：不再堆在界面上）
        var argsHint = new Label
        {
            Name = "cliArgsHint",
            Text = "写法与 mediac CLI 一致；全部参数与示例见「使用说明…」",
            ForeColor = Color.Gray,
            AutoSize = true,
            Location = new Point(80, 92),
        };

        overrideCheck.Text = "覆盖已有";
        overrideCheck.SetBounds(16, 122, 86, 20);
        strictCheck.Text = "严格模式";
        strictCheck.SetBounds(108, 122, 86, 20);
        debugCheck.Text = "详细日志";
        debugCheck.SetBounds(200, 122, 86, 20);
        animeCheck.Text = "动漫模式";
        animeCheck.SetBounds(292, 122, 86, 20);
        syncLogCheck.Text = "同步日志到输出目录";
        syncLogCheck.SetBounds(384, 122, 168, 20);

        box.Controls.AddRange([
            lblPreset, presetCombo, lblHw, hwaccelCombo, lblDec, decodeCombo, btnAbout,
            lblArgs, cliArgsBox, argsHint,
            overrideCheck, strictCheck, debugCheck, animeCheck, syncLogCheck,
        ]);
        Controls.Add(box);
    }

    private void BuildActionGroup()
    {
        var box = new GroupBox
        {
            Text = "操作",
            Location = new Point(12, 396),
            Size = new Size(976, 64),
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
            Location = new Point(12, 468),
            Size = new Size(976, 288),
            Anchor = AnchorStyles.Top | AnchorStyles.Bottom | AnchorStyles.Left | AnchorStyles.Right,
        };

        logBox.SetBounds(14, 24, 948, 226);
        logBox.Anchor = AnchorStyles.Top | AnchorStyles.Bottom | AnchorStyles.Left | AnchorStyles.Right;
        logBox.ReadOnly = true;
        logBox.BackColor = Color.FromArgb(250, 250, 250);
        logBox.Font = new Font("Consolas", 9F);
        // 需求：日志自动换行，不要横向滚动条。
        // 命令行长，靠自动换行展示比横向拖动更易读。
        logBox.WordWrap = true;
        logBox.ScrollBars = RichTextBoxScrollBars.Vertical;
        logBox.DetectUrls = false;

        progressBar.SetBounds(14, 258, 700, 18);
        progressBar.Anchor = AnchorStyles.Bottom | AnchorStyles.Left | AnchorStyles.Right;
        progressBar.Minimum = 0;
        progressBar.Maximum = 100;

        // 进度文本为文件级（如「122/200」）；文件内部的百分比与 speed 只写日志
        progressLabel.Text = "就绪";
        progressLabel.SetBounds(722, 259, 240, 18);
        progressLabel.Anchor = AnchorStyles.Bottom | AnchorStyles.Right;
        progressLabel.TextAlign = ContentAlignment.MiddleRight;

        box.Controls.AddRange([logBox, progressBar, progressLabel]);
        Controls.Add(box);
    }

    private void BuildStatusBar()
    {
        systemInfoLabel.Spring = true;
        systemInfoLabel.TextAlign = ContentAlignment.MiddleLeft;
        systemInfoLabel.Text = "正在探测机器与 ffmpeg 信息…";

        collectLabel.BorderSides = ToolStripStatusLabelBorderSides.Left;
        collectLabel.Text = "未收集";

        stateLabel.BorderSides = ToolStripStatusLabelBorderSides.Left;
        stateLabel.Text = "就绪";

        statusBar.Items.AddRange([systemInfoLabel, collectLabel, stateLabel]);
        statusBar.SizingGrip = false;
        Controls.Add(statusBar);
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

    /// <summary>「使用说明」窗口的显示（供测试覆写，避免模态阻塞）。</summary>
    protected virtual void ShowAboutDialog()
    {
        using var dlg = new AboutForm();
        dlg.ShowDialog(this);
    }

    private void OnShowAbout(object? sender, EventArgs e) => ShowAboutDialog();

    // ==================================================================
    // 输入
    // ==================================================================

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
        if (paths.Count == 0) { collectLabel.Text = "未收集"; return; }
        collectLabel.Text = "正在扫描输入…";
        var opts = ReadOptions();
        opts.Inputs.Clear();
        opts.Inputs.AddRange(paths);
        Task.Run(() =>
        {
            string msg;
            try
            {
                var files = TranscodeSession.CollectFiles(opts);
                var bytes = files.Sum(f => f.Size);
                msg = $"已收集 {files.Count} 个文件  ·  {Helper.HumanSize(bytes)}";
            }
            catch (Exception ex) { msg = $"扫描失败：{ex.Message}"; }
            SafeUi(() => collectLabel.Text = msg);
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
        CliArgs = cliArgsBox.Text.Trim(),
        Override = overrideCheck.Checked,
        Strict = strictCheck.Checked,
        Debug = debugCheck.Checked,
        Anime = animeCheck.Checked,
        SyncLogToOutput = syncLogCheck.Checked,
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

        // 参数告警必须在开跑前摆到台面上。
        // 未知参数、GUI 不支持的能力、无效写法（如 ffargs 里 vb=3M）若被静默忽略，
        // 用户会以为参数已经生效——这比直接报错更糟。
        var warnings = opts.AllWarnings();
        if (warnings.Count > 0)
        {
            var detail = string.Join(Environment.NewLine + "  · ", warnings);
            if (!Confirm(
                "以下参数不会生效或被调整：" + Environment.NewLine + Environment.NewLine +
                "  · " + detail + Environment.NewLine + Environment.NewLine +
                "仍要继续吗？（这些项将被忽略，其余参数正常应用）",
                "参数提示")) return;
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
        progressLabel.Text = doit ? "准备中…" : "预览中…";
        stateLabel.Text = doit ? "执行中" : "预览中";
        droppedWarned = false;
        lastOutputDir = string.IsNullOrWhiteSpace(opts.Output) ? null : opts.Output;
        SetBusy(true);

        // 每次运行单独一份日志文件，便于按次取证
        logWriter?.Dispose();
        logWriter = new SessionLogWriter();

        AppendLog(SessionLogLevel.Info, doit ? "=== 开始转码 ===" : "=== 预览命令（不写盘）===");
        AppendLog(SessionLogLevel.Info, $"输入 {opts.Inputs.Count} 项  ·  输出目录 " +
            (string.IsNullOrWhiteSpace(opts.Output) ? "（源文件目录）" : opts.Output));
        foreach (var w in warnings) AppendLog(SessionLogLevel.Warn, w);

        cts = new CancellationTokenSource();
        var token = cts.Token;

        // core 的执行是同步的（内部 WaitForExit），必须整体放到后台线程，否则 UI 冻死。
        running = Task.Run(() =>
        {
            var session = new TranscodeSession(
                (lvl, text) =>
                {
                    sink.Write(lvl, text);
                    logWriter?.Write(lvl, text, DateTime.Now);
                },
                OnProgress);
            try
            {
                var summary = session.Run(opts, doit, token);
                summary.SuppressedLines = session.SuppressedLines;
                return summary;
            }
            catch (Exception ex)
            {
                sink.Write(SessionLogLevel.Error, $"未处理异常：{ex.Message}");
                return new SessionSummary();
            }
        });

        running.ContinueWith(t => SafeUi(() => FinishRun(t, opts)), TaskScheduler.Default);
    }

    /// <summary>
    /// 进度回调（来自 core 的回调线程）。
    ///
    /// 需求：进度条以<b>文件</b>为单位（如 122/200），文件内部的百分比与 speed 只写日志。
    /// 因此这里只记录文件级信息；逐文件百分比与 speed 已由编排层写入日志。
    /// </summary>
    private void OnProgress(SessionProgress p)
    {
        lastPercent = (int)Math.Round(p.OverallPercent);
        lastFileIndex = p.FileIndex;
        lastFileTotal = p.FileTotal;
        lastFileName = p.FileName;
        progressDirty = true;
    }

    private void FinishRun(Task<SessionSummary> task, GuiOptions opts)
    {
        running = null;
        try { cts?.Dispose(); } catch { /* ignore */ }
        cts = null;
        SetBusy(false);

        if (task.IsFaulted)
        {
            AppendLog(SessionLogLevel.Error,
                $"任务异常：{task.Exception?.GetBaseException().Message}");
            stateLabel.Text = "已结束（异常）";
            progressLabel.Text = "异常结束";
            return;
        }
        if (!task.IsCompletedSuccessfully)
        {
            AppendLog(SessionLogLevel.Warn, "任务已取消。");
            progressBar.Value = 0;
            progressLabel.Text = "已取消";
            stateLabel.Text = "已取消";
            return;
        }

        var s = task.Result;
        if (s.Total == 0)
        {
            progressLabel.Text = "无处理项";
            stateLabel.Text = "无处理项";
            FinalizeLog(opts, s);
            return;
        }

        progressBar.Value = 100;
        // 文案按模式区分：预览不产出文件，Processed 恒为 0，
        // 若显示「完成 0/1」会让用户以为失败——预览应显示「预览 1/1」。
        progressLabel.Text = s.WasCancelled
            ? "已取消"
            : s.Preview > 0
                ? $"预览 {s.Preview}/{s.Total}"
                : $"完成 {s.Processed}/{s.Total}";
        stateLabel.Text = s.WasCancelled ? "已取消" : s.Preview > 0 ? "预览完成" : "已完成";

        FinalizeLog(opts, s);
    }

    /// <summary>
    /// 收尾：写入统计块（需求 6）、按需同步日志到输出目录（需求 7）。
    /// </summary>
    private void FinalizeLog(GuiOptions opts, SessionSummary summary)
    {
        string? synced = null;
        if (opts.SyncLogToOutput && !string.IsNullOrWhiteSpace(opts.Output))
        {
            synced = logWriter?.SyncTo(opts.Output);
        }

        var report = SessionReport.Build(
            summary,
            inputDir: FirstInputDir(opts),
            outputDir: string.IsNullOrWhiteSpace(opts.Output) ? null : opts.Output,
            logPath: logWriter?.TempPath,
            syncedPath: synced,
            logError: logWriter?.WriteError);

        // 统计块经 sink 写入，因此也会落进日志文件本身
        AppendLog(SessionLogLevel.Info, report);
        Flush();
    }

    private static string? FirstInputDir(GuiOptions opts)
    {
        var first = opts.Inputs.FirstOrDefault();
        if (string.IsNullOrEmpty(first)) return null;
        if (Directory.Exists(first)) return first;
        return File.Exists(first) ? Path.GetDirectoryName(first) : first;
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
            if (running is not null && lastFileTotal > 0)
            {
                // 文件级读数（需求：显示「正在处理 122/200」），不含文件内部百分比与 speed
                progressLabel.Text = $"正在处理 {lastFileIndex}/{lastFileTotal}  {lastFileName}";
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
            if (logBox.Visible)
            {
                logBox.SelectionStart = logBox.TextLength;
                logBox.ScrollToCaret();
            }
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
        SessionLogLevel.Progress => "· ",
        _ => "  ",
    };

    private static Color ColorFor(SessionLogLevel level) => level switch
    {
        SessionLogLevel.Cmd => Color.FromArgb(0x0B, 0x53, 0x94),
        SessionLogLevel.Warn => Color.FromArgb(0xA6, 0x6A, 0x00),
        SessionLogLevel.Error => Color.FromArgb(0xB0, 0x1E, 0x1E),
        SessionLogLevel.Done => Color.FromArgb(0x1B, 0x6B, 0x2F),
        // 文件内部进度是高频行，弱化为灰色，避免与结论行争夺注意力
        SessionLogLevel.Progress => Color.FromArgb(0x80, 0x80, 0x80),
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
        progressLabel.Text = running is null ? "就绪" : progressLabel.Text;
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
    /// 首屏告诉用户「实际在用哪个 ffmpeg、走的哪一层」，并填充状态栏。
    /// 用户永远能自证环境，而不是遇到问题时无从下手。
    /// </summary>
    private void StartupDiagnostics()
    {
        try
        {
            var names = TranscodeSession.PresetNames();
            presetCombo.Items.Clear();
            presetCombo.Items.AddRange(names.Cast<object>().ToArray());
            var idx = presetCombo.Items.IndexOf("hevc_2k");
            presetCombo.SelectedIndex = idx >= 0 ? idx : 0;
            AppendLog(SessionLogLevel.Info, $"已加载 {names.Count} 个预设（详细说明见「使用说明…」）。");
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
            stateLabel.Text = "缺少 ffmpeg";
            systemInfoLabel.Text = "未找到 ffmpeg";
            return;
        }
        FfmpegRun.SetFFmpegPath(ffmpeg);
        AppendLog(SessionLogLevel.Info, $"ffmpeg: {ffmpeg}");

        var ffprobe = FfmpegBin.ResolveFFprobeBinary(ffmpeg);
        AppendLog(SessionLogLevel.Info,
            ffprobe is null ? "ffprobe: 未找到（媒体信息探测会失败）" : $"ffprobe: {ffprobe}");

        // 状态栏：CPU / GPU / ffmpeg 版本 / 可用硬件层（需求 4）
        // 悬停显示完整信息（多个适配器时状态栏只列主适配器，细节放 ToolTip）
        SystemInfoProbe.ProbeAsync(
            ffmpeg,
            info => SafeUi(() =>
            {
                systemInfoLabel.Text = info.ToStatusLine();
                systemInfoLabel.ToolTipText = info.ToFullText();
            }),
            err => sink.Write(SessionLogLevel.Warn, $"硬件能力探测失败（不影响 cpu 层转码）：{err}"));
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
            try { logWriter?.Dispose(); } catch { /* ignore */ }
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

