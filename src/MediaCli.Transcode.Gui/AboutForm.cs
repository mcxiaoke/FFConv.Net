using System.Text;
using MediaCli.Transcode.Model;
using MediaCli.Transcode.Presets;

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
        sb.AppendLine("mediac 命令行参数（与 mediacli.js 的 cmd ffmpeg 对齐）");
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
        sb.AppendLine("一、参数优先级（与 mediac 一致）");
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
        sb.AppendLine("    压低体积：      --video-quality 26 --max-bitrate 4M");
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
        Text = "使用说明 — 预设与命令行参数";
        ClientSize = new Size(860, 620);
        MinimumSize = new Size(680, 460);
        StartPosition = FormStartPosition.CenterParent;
        Font = new Font("Microsoft YaHei UI", 9F);

        var tabs = new TabControl { Name = "aboutTabs", Dock = DockStyle.Fill };

        tabs.TabPages.Add(BuildPresetPage());
        tabs.TabPages.Add(BuildCliPage());
        tabs.TabPages.Add(BuildTipsPage());

        Controls.Add(tabs);

        var close = new Button
        {
            Name = "btnAboutClose",
            Text = "关闭",
            DialogResult = DialogResult.OK,
        };
        close.SetBounds(ClientSize.Width - 110, ClientSize.Height - 40, 96, 28);
        close.Anchor = AnchorStyles.Bottom | AnchorStyles.Right;
        close.Click += (_, _) => Close();
        Controls.Add(close);

        // 缩放基准：与 MainForm 同理，纯代码创建的 Form 默认完全不缩放，
        // 在 >96 DPI 下会挤坏固定像素布局。必须在 Font 与布局之后设置。
        AutoScaleMode = AutoScaleMode.Dpi;
        AutoScaleDimensions = new SizeF(96F, 96F);
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
        if (list.Items.Count > 0) list.Items[0].Selected = true;

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
        list.Columns.Add("参数", 180);
        list.Columns.Add("取值", 130);
        list.Columns.Add("说明", 400);
        list.Columns.Add("状态", 90);

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
