using System.Text;
using MediaCli.Transcode.Model;
using MediaCli.Transcode.Presets;
using MediaCli.Transcode.Support;

// 消歧：Model 命名空间也有一个 Size（视频尺寸），而本文件里的 Size 都指窗口尺寸。
using Size = System.Drawing.Size;

namespace MediaCli.Transcode.Gui;

/// <summary>
/// 使用说明的文本生成（与界面分离，便于测试与「复制」）。
///
/// 单一事实源：预设来自 <see cref="FFmpegPresets"/>，命令行参数来自
/// <see cref="CliOptions"/>，因此说明与实际行为不会各自漂移。
/// </summary>
public static class AboutContent
{
    /// <summary>预设清单。</summary>
    public static IReadOnlyList<FFmpegPreset> Presets()
    {
        TranscodeSession.EnsurePresetsLoaded();
        return FFmpegPresets.GetAllPresets()
            .OrderBy(kv => kv.Key, StringComparer.Ordinal)
            .Select(kv => kv.Value)
            .ToList();
    }

    /// <summary>单个预设的参数明细（选中预设时展示）。</summary>
    public static string PresetDetail(FFmpegPreset p)
    {
        var sb = new StringBuilder();
        void Row(string k, string? v)
        {
            if (!string.IsNullOrWhiteSpace(v)) sb.AppendLine($"{k}：{v}");
        }

        Row("预设名", p.Name);
        Row("类型", p.Type);
        Row("输出容器", p.Format);
        Row("编码族", p.VideoCodecFamily);
        Row("输出目录", p.Output);
        Row("文件名前缀", p.Prefix);
        Row("文件名后缀", p.Suffix);
        Row("目标长边", p.Dimension > 0 ? p.Dimension.ToString() : null);
        Row("视频质量", p.VideoQuality > 0 ? p.VideoQuality.ToString("0.##") : null);
        Row("视频码率", p.VideoBitrate > 0 ? $"{p.VideoBitrate / 1000}k" : null);
        Row("峰值码率", p.MaxBitrate > 0 ? $"{p.MaxBitrate / 1000}k" : null);
        Row("音频编码器", p.AudioCodec);
        Row("音频码率", p.AudioBitrate > 0 ? $"{p.AudioBitrate / 1000}k" : null);
        Row("音频质量", p.AudioQuality > 0 ? p.AudioQuality.ToString("0.##") : null);
        Row("帧率", p.Framerate > 0 ? p.Framerate.ToString("0.###") : null);
        Row("变速", p.Speed != 1 ? p.Speed.ToString("0.##") : null);
        Row("智能码率", p.SmartBitrate == true ? "是" : null);
        Row("缩放前滤镜", p.PreFilters);
        Row("缩放后滤镜", p.PostFilters);
        Row("滤镜", p.Filters);
        Row("输入参数", p.InputArgs);
        Row("输出参数", p.OutputArgs);

        if (sb.Length == 0) sb.AppendLine("（无参数）");
        return sb.ToString();
    }

    /// <summary>命令行参数总览（纯文本，用于「复制」）。</summary>
    public static string CliOptionTable()
    {
        var sb = new StringBuilder();
        sb.AppendLine("FFConv 命令行参数（与 FFConv CLI 对齐）");
        sb.AppendLine();
        sb.AppendLine("【可用参数】");
        foreach (var o in CliOptions.All.Where(o => o.Supported))
        {
            var aliases = o.Aliases is { Length: > 0 } ? $"（别名 {string.Join(" / ", o.Aliases)}）" : "";
            var value = o.Kind == CliValueKind.Flag ? "开关" : $"取值：{KindText(o.Kind)}";
            sb.AppendLine($"  --{o.Name}{aliases}  {value}");
            sb.AppendLine($"      {o.Description}");
            if (o.ShadowedBy is not null)
            {
                sb.AppendLine($"      注意：会覆盖界面控件 {o.ShadowedBy}（显式参数优先）");
            }
        }

        sb.AppendLine();
        sb.AppendLine("【本 GUI 不支持】");
        foreach (var o in CliOptions.All.Where(o => !o.Supported))
        {
            sb.AppendLine($"  --{o.Name}  {o.Description}");
            sb.AppendLine($"      原因：{o.Reason}");
        }

        return sb.ToString();
    }

    private static string KindText(CliValueKind kind) => kind switch
    {
        CliValueKind.Number => "数字",
        CliValueKind.Bitrate => "码率（纯数字 bps 或 3M / 800k）",
        CliValueKind.Choice => "限定取值",
        _ => "文本",
    };

    /// <summary>使用提示与常见示例。</summary>
    public static string UsageTips()
    {
        var sb = new StringBuilder();
        sb.AppendLine($"FFConv {BuildInfo.DisplayString}");
        sb.AppendLine();
        sb.AppendLine("【核心工具链环境】");
        var ffmpeg = MediaCli.Transcode.Bin.FfmpegBin.ResolveFFmpegBinary();
        var ffprobe = MediaCli.Transcode.Bin.FfmpegBin.ResolveFFprobeBinary(ffmpeg);
        var mediainfo = MediaCli.Transcode.Bin.FfmpegBin.ResolveMediaInfoBinary(ffmpeg);
        sb.AppendLine($"    ffmpeg:    {(string.IsNullOrEmpty(ffmpeg) ? "未找到（请设置 FFMPEG_PATH 环境变量或放入 exe 同级/ffmpeg/ 目录）" : ffmpeg)}");
        sb.AppendLine($"    ffprobe:   {(string.IsNullOrEmpty(ffprobe) ? "未找到" : ffprobe)}");
        sb.AppendLine($"    mediainfo: {(string.IsNullOrEmpty(mediainfo) ? "未找到（可选，优先使用 ffprobe 探测）" : mediainfo)}");
        sb.AppendLine();
        sb.AppendLine("一、参数优先级（与 FFConv 一致）");
        sb.AppendLine("    命令行单独参数  >  --ffargs 复合参数  >  预设默认值");
        sb.AppendLine("    本界面上：参数框  >  界面控件（下拉框 / 复选框）");
        sb.AppendLine("    参数框覆盖了某个控件时，日志里会明确说明，不会静默生效。");
        sb.AppendLine();
        sb.AppendLine("二、码率怎么写");
        sb.AppendLine("    --video-bitrate 3000000   裸数字 = bps（推荐）");
        sb.AppendLine("    --video-bitrate 3M        带单位：k / m / g");
        sb.AppendLine("    --ffargs 里的码率必须写裸 bps（vb=3000000），");
        sb.AppendLine("    写 vb=3M 会被 core 静默丢弃——界面会提前告警。");
        sb.AppendLine();
        sb.AppendLine("三、常见示例");
        // 示例里的参数必须真实存在：曾写成 --max-bitrate（CliOptions 未定义），
        // 用户照抄只会得到"未知参数，已忽略"。由 UsageTips_OnlyMentionsDefinedParams 锁住。
        sb.AppendLine("    压低体积：      --video-quality 26 --audio-bitrate 128k");
        sb.AppendLine("    强制软解：      --decode-mode cpu");
        sb.AppendLine("    只处理部分文件：--include 1080 --exclude sample");
        sb.AppendLine("    改名归档：      --prefix \"[转码] \" --suffix \"_{preset}\"");
        sb.AppendLine("    变速：          --speed 1.5    （范围 0.5–2.0，音视频同步）");
        sb.AppendLine("    指定编码器：    --video-codec libx265");
        sb.AppendLine();
        sb.AppendLine("四、日志");
        sb.AppendLine("    ffmpeg 的常规输出默认不显示，只保留错误与警告；");
        sb.AppendLine("    需要完整输出时勾选「详细日志」。");
        sb.AppendLine("    日志自动换行，命令行长也无需横向拖动。");
        sb.AppendLine("    任务结束后会在日志末尾给出统计信息与日志文件位置；");
        sb.AppendLine("    勾选「同步日志到输出目录」可随产物归档一份。");
        sb.AppendLine();
        sb.AppendLine("五、关于预设");
        sb.AppendLine("    预设 = 一组预定义参数，选定后再用上面的参数做覆盖。");
        sb.AppendLine("    以 _ 开头的是继承基类，不会出现在下拉框中。");
        sb.AppendLine("    自定义预设可写入 ~/.mediac/presets.yaml。");
        sb.AppendLine();
        sb.AppendLine("六、同基名的多容器文件");
        sb.AppendLine("    同一目录下若存在 Movie.mkv / Movie.mp4 / Movie.webm，");
        sb.AppendLine("    默认后缀只保留基名，三者会映射到同一个输出名：");
        sb.AppendLine("    只有第一个能产出，其余会被跳过并记为");
        sb.AppendLine("    destination_conflict_in_batch（日志会指出被谁占用）。");
        sb.AppendLine("    需要全部保留时用源扩展名区分：");
        sb.AppendLine("        自定义参数填 --suffix \"_{preset}{srcExt}\"");
        sb.AppendLine("    可用模板变量：{srcExt} .mkv | {srcExtBare} mkv |");
        sb.AppendLine("    {srcStem} 源文件名 | {srcName} 源文件名含扩展名。");
        sb.AppendLine("    注意 {srcVideoCodec} / {srcFormat} 不可靠——");
        sb.AppendLine("    同内容的 .mkv 与 .mp4 可能是相同容器或同一 codec。");
        return sb.ToString();
    }
}

/// <summary>
/// 使用说明窗口（「使用说明」按钮）。
///
/// 内容全部由 <see cref="AboutContent"/> 生成，本窗口只负责呈现，
/// 因此说明文字可被单元测试直接断言，无需驱动界面。
/// </summary>
public sealed class AboutForm : Form
{
    public AboutForm()
    {
        Text = $"使用说明 — 预设与命令行参数 (v{BuildInfo.AppVersion})";
        // 窗口宽度需容得下「说明」列的最长文案（自适应列宽），
        // 否则会出现横向滚动条——参考表应尽量一屏可读。
        ClientSize = new Size(1140, 660);
        MinimumSize = new Size(820, 480);
        StartPosition = FormStartPosition.CenterParent;
        Font = new Font("Microsoft YaHei UI", 9F);

        // 缩放基准必须在任何布局之前设置：纯代码创建的 Form 默认 AutoScaleMode.Inherit
        // 且 AutoScaleDimensions=(0,0)，在 >96 DPI 下字体放大而控件尺寸不变。
        AutoScaleMode = AutoScaleMode.Dpi;
        AutoScaleDimensions = new SizeF(96F, 96F);

        var tabs = new TabControl { Name = "aboutTabs", Dock = DockStyle.Fill };

        // 命令行参数放第一个 TAB
        tabs.TabPages.Add(BuildCliPage());
        tabs.TabPages.Add(BuildPresetPage());
        tabs.TabPages.Add(BuildTipsPage());

        // 关闭按钮放在 Dock=Bottom 的独立面板里。
        //
        // 不能直接把按钮 Add 到窗体：TabControl 用 Dock=Fill 覆盖整个客户区，
        // 而 WinForms 的 z-order 规则是「后添加的位于顶层、Controls[0] 才最顶层」，
        // 因此按钮会被 TabControl 完整盖住——运行期不可见、也点不到
        // （实测 GetChildAtPoint(按钮中心) 命中的是 aboutTabs）。
        // 用 Dock 分区隔开后两者区域不再重叠，从根本上消除遮挡。
        var footer = new Panel
        {
            Name = "aboutFooter",
            Dock = DockStyle.Bottom,
            Height = 44,
        };
        var fPath = MediaCli.Transcode.Bin.FfmpegBin.ResolveFFmpegBinary();
        var pPath = MediaCli.Transcode.Bin.FfmpegBin.ResolveFFprobeBinary(fPath);
        var mPath = MediaCli.Transcode.Bin.FfmpegBin.ResolveMediaInfoBinary(fPath);
        var toolStatus = new Label
        {
            Name = "lblAboutToolStatus",
            AutoSize = false,
            TextAlign = ContentAlignment.MiddleLeft,
            ForeColor = Color.DimGray,
            Font = new Font("Microsoft YaHei UI", 8.25F),
            Location = new Point(12, 6),
            Size = new Size(footer.Width - 130, 32),
            Anchor = AnchorStyles.Top | AnchorStyles.Bottom | AnchorStyles.Left | AnchorStyles.Right,
            Text = $"ffmpeg: {Path.GetFileName(fPath ?? "未找到")}   ·   ffprobe: {Path.GetFileName(pPath ?? "未找到")}   ·   mediainfo: {Path.GetFileName(mPath ?? "未找到")}",
        };
        var toolTip = new ToolTip();
        toolTip.SetToolTip(toolStatus,
            $"ffmpeg 路径：{fPath ?? "未找到"}\n" +
            $"ffprobe 路径：{pPath ?? "未找到"}\n" +
            $"mediainfo 路径：{mPath ?? "未找到"}");

        var close = new Button
        {
            Name = "btnAboutClose",
            Text = "关闭",
            DialogResult = DialogResult.OK,
        };
        close.SetBounds(footer.Width - 110, 8, 96, 28);
        close.Anchor = AnchorStyles.Bottom | AnchorStyles.Right;
        close.Click += (_, _) => Close();
        footer.Controls.Add(toolStatus);
        footer.Controls.Add(close);

        // 先加 Fill 再加 Bottom，保证停靠布局按预期分配剩余空间。
        Controls.Add(tabs);
        Controls.Add(footer);

        AcceptButton = close;
        CancelButton = close;

        // ListView 的 ColumnHeader.Width 属于"集合内的数值属性"，
        // AutoScaleDimensions 不会缩放它——构造期 DeviceDpi 也还是默认 96。
        // 因此在 Shown（DPI 已确定）时统一按比例换算一次，
        // 否则 150% DPI 下字体会放大而列宽保持 96 DPI 原值，长说明文本被截成省略号。
        Shown += (_, _) => ScaleListColumns(tabs);
        DpiChanged += (_, _) => ScaleListColumns(tabs);
    }

    /// <summary>
    /// 把 AboutForm 内所有 ListView 的列宽从 96 DPI 设计值换算到当前 DPI。
    /// 用 Tag 记住设计基准值，保证多次调用（DpiChanged 会重复触发）不会累计放大。
    /// </summary>
    private void ScaleListColumns(Control root)
    {
        var factor = DeviceDpi / 96.0;
        foreach (var list in EnumerateListViews(root))
        {
            foreach (ColumnHeader col in list.Columns)
            {
                // 负值（-1/-2）是 ListView 的"按内容/表头自适应"标记，必须原样保留，
                // 否则会被换算成一个具体像素值而失去自适应能力。
                if (col.Width < 0) continue;

                if (col.Tag is not int baseWidth)
                {
                    baseWidth = col.Width;
                    col.Tag = baseWidth;
                }
                var target = (int)Math.Round(baseWidth * factor);
                if (col.Width != target) col.Width = target;
            }
        }
    }

    private static IEnumerable<ListView> EnumerateListViews(Control root)
    {
        foreach (Control child in root.Controls)
        {
            if (child is ListView lv) yield return lv;
            foreach (var nested in EnumerateListViews(child)) yield return nested;
        }
    }

    private static TabPage BuildPresetPage()
    {
        var page = new TabPage("预设") { Name = "tabPresets" };

        var list = new ListView
        {
            Name = "presetList",
            View = View.Details,
            FullRowSelect = true,
            MultiSelect = false,
            Dock = DockStyle.Top,
            Height = 300,
        };
        list.Columns.Add("预设", 150);
        list.Columns.Add("类型", 60);
        list.Columns.Add("容器", 60);
        list.Columns.Add("编码族", 70);
        list.Columns.Add("长边", 60);
        list.Columns.Add("质量", 50);
        list.Columns.Add("音频", 110);

        foreach (var p in AboutContent.Presets())
        {
            var item = new ListViewItem(p.Name);
            item.SubItems.Add(p.Type ?? "-");
            item.SubItems.Add(p.Format ?? "-");
            item.SubItems.Add(p.VideoCodecFamily ?? "-");
            item.SubItems.Add(p.Dimension > 0 ? p.Dimension.ToString() : "-");
            item.SubItems.Add(p.VideoQuality > 0 ? p.VideoQuality.ToString("0.##") : "-");
            item.SubItems.Add($"{p.AudioCodec}/{p.AudioBitrate / 1000}k");
            item.Tag = p;
            list.Items.Add(item);
        }

        var detail = new RichTextBox
        {
            Name = "presetDetail",
            ReadOnly = true,
            Dock = DockStyle.Fill,
            Font = new Font("Consolas", 9F),
            BackColor = Color.FromArgb(250, 250, 250),
            WordWrap = false,
            ScrollBars = RichTextBoxScrollBars.Vertical,
        };

        list.SelectedIndexChanged += (_, _) =>
        {
            if (list.SelectedItems.Count == 0) return;
            detail.Text = AboutContent.PresetDetail((FFmpegPreset)list.SelectedItems[0].Tag!);
        };
        if (list.Items.Count > 0)
        {
            list.Items[0].Selected = true;
            detail.Text = AboutContent.PresetDetail((FFmpegPreset)list.Items[0].Tag!);
        }

        page.Controls.Add(detail);
        page.Controls.Add(list);
        return page;
    }

    private static TabPage BuildCliPage()
    {
        var page = new TabPage("命令行参数") { Name = "tabCli" };

        var list = new ListView
        {
            Name = "cliOptionList",
            View = View.Details,
            FullRowSelect = true,
            Dock = DockStyle.Fill,
        };
        // 各列基准宽度按 96 DPI 设计值给出；「说明」列用 -2 让 WinForms 按
        // 表头与内容的实际宽度自适应（该列最长），避免高 DPI 下出现省略号。
        // 「取值」列内容都很短（最长 "bps / 3M / 800k"），收窄后把余量让给「说明」列。
        list.Columns.Add("参数", 230);
        list.Columns.Add("取值", 110);
        list.Columns.Add("说明", -2);
        list.Columns.Add("状态", 80);

        foreach (var o in CliOptions.All)
        {
            var aliases = o.Aliases is { Length: > 0 } ? $"  ({string.Join("/", o.Aliases)})" : "";
            var value = o.Kind switch
            {
                CliValueKind.Flag => "开关",
                CliValueKind.Number => "数字",
                CliValueKind.Bitrate => "bps / 3M / 800k",
                CliValueKind.Choice => string.Join("/", o.Choices ?? []),
                _ => "文本",
            };

            var item = new ListViewItem($"--{o.Name}{aliases}");
            item.SubItems.Add(value);
            item.SubItems.Add(o.Description);
            item.SubItems.Add(o.Supported ? "可用" : "不支持");
            if (!o.Supported) item.ForeColor = Color.Gray;
            list.Items.Add(item);
        }

        page.Controls.Add(list);
        return page;
    }

    private static TabPage BuildTipsPage()
    {
        var page = new TabPage("使用提示") { Name = "tabTips" };
        var box = new RichTextBox
        {
            Name = "tipsBox",
            ReadOnly = true,
            Dock = DockStyle.Fill,
            Font = new Font("Microsoft YaHei UI", 9F),
            BackColor = Color.FromArgb(250, 250, 250),
            WordWrap = true,
            ScrollBars = RichTextBoxScrollBars.Vertical,
            Text = AboutContent.UsageTips(),
        };
        page.Controls.Add(box);
        return page;
    }
}
