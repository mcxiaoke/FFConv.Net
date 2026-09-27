using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Windows.Forms;
using MediaCli.Transcode.Presets;

namespace MediaCli.Transcode.Gui;

/// <summary>
/// 高级参数面板（横向流式折行排布方案）。
/// 
/// 布局架构：
/// - 顶部：当前预设详细参数信息（单行紧凑呈现）。
/// - 上部：参数配置流（FlowLayoutPanel，每个参数与输入框作为原子单元横向排列，加分隔符，自动换行）。
/// - 中部：参数用法说明区（集中展示各参数说明、取值范围与别名，可垂直滚动）。
/// - 底部：参数试算命令行回显（ffmpeg）、写入参数框文本、状态提示与操作按钮（贴底排布）。
/// </summary>
public class ParamsFlowForm : Form
{
    private readonly string presetName;
    private readonly Dictionary<string, string?> values = new(StringComparer.Ordinal);

    private readonly Panel headerPanel = new()
    {
        Name = "headerPanel",
        Dock = DockStyle.Top
    };

    private readonly Label presetLbl = new()
    {
        Name = "paramPresetLabel",
        AutoSize = true
    };

    private readonly SplitContainer splitBody = new()
    {
        Name = "paramSplitBody",
        Orientation = Orientation.Horizontal,
        Dock = DockStyle.Fill
    };

    private readonly FlowLayoutPanel flowPanel = new()
    {
        Name = "paramFlowPanel",
        Dock = DockStyle.Fill,
        FlowDirection = FlowDirection.LeftToRight,
        WrapContents = true,
        AutoScroll = true,
        BackColor = Color.FromArgb(250, 250, 250)
    };

    private readonly TextBox descBox = new()
    {
        Name = "paramDescBox",
        Dock = DockStyle.Fill,
        Multiline = true,
        ReadOnly = true,
        ScrollBars = ScrollBars.Vertical,
        BackColor = Color.FromArgb(253, 253, 253),
        ForeColor = Color.FromArgb(60, 60, 60),
        BorderStyle = BorderStyle.FixedSingle
    };

    private readonly Panel footerPanel = new()
    {
        Name = "footerPanel"
    };

    private readonly Label lblTrial = new()
    {
        Name = "lblParamTrial",
        Text = "参数示例（示意命令，非最终命令；真实命令用主界面的「预览命令」）",
        AutoSize = true
    };

    private readonly TextBox trialBox = new()
    {
        Name = "paramTrialBox"
    };

    private readonly Label lblPreview = new()
    {
        Name = "lblParamPreview",
        Text = "写入参数框的文本（可直接复制到命令行）",
        AutoSize = true
    };

    private readonly TextBox previewBox = new()
    {
        Name = "paramPreviewBox"
    };

    private readonly Label statusLabel = new()
    {
        Name = "paramStatusLabel",
        AutoSize = true
    };

    private readonly Button btnEditMode = new()
    {
        Name = "btnParamEditMode"
    };

    private readonly Button btnCopy = new()
    {
        Name = "btnParamCopy"
    };

    private readonly Button btnReset = new()
    {
        Name = "btnParamReset"
    };

    private readonly Button btnCancel = new()
    {
        Name = "btnParamCancel"
    };

    private readonly Button btnSave = new()
    {
        Name = "btnParamSave"
    };

    private readonly ToolTip tip = new();
    private readonly System.Windows.Forms.Timer trialTimer = new();

    private bool editing;
    private bool hasLoadWarnings;
    private bool layoutReady;
    private bool reflowingFooter;

    public string CliArgs { get; private set; } = "";

    public ParamsFlowForm(string presetName, string cliArgs)
    {
        this.presetName = presetName;
        Text = "高级参数";
        ClientSize = new Size(960, 720);
        MinimumSize = new Size(800, 560);
        StartPosition = FormStartPosition.CenterParent;
        Font = new Font("Microsoft YaHei UI", 9f);

        var deserializeResult = ParamSerializer.Deserialize(cliArgs);
        foreach (var (k, v) in deserializeResult.Values)
        {
            values[k] = v;
        }

        CliArgs = cliArgs.Trim();
        hasLoadWarnings = deserializeResult.Warnings.Count > 0;

        BuildBody();
        BuildFooter();
        BuildHeader();

        previewBox.Text = CliArgs;
        if (hasLoadWarnings)
        {
            EnterEditMode($"有 {deserializeResult.Warnings.Count} 项参数无法解析，请先修正后再保存。");
        }

        RebuildFlowAndDocs();
        UpdateTrial();

        trialTimer.Interval = 250;
        trialTimer.Tick += (_, _) =>
        {
            trialTimer.Stop();
            UpdateTrial();
        };

        Shown += (_, _) =>
        {
            layoutReady = true;
            ReflowHeader();
            ReflowFooter();
            splitBody.SplitterDistance = Scale(200);
            flowPanel.AutoScrollPosition = new Point(0, 0);
        };

        AutoScaleMode = AutoScaleMode.Dpi;
        AutoScaleDimensions = new SizeF(96f, 96f);
    }

    private void BuildHeader()
    {
        headerPanel.Height = Scale(32);
        headerPanel.Resize += (_, _) => ReflowHeader();

        presetLbl.Text = FormatPresetSummary(presetName);
        tip.SetToolTip(presetLbl, presetLbl.Text);

        headerPanel.Controls.Add(presetLbl);
        Controls.Add(headerPanel);
    }

    private void ReflowHeader()
    {
        if (!layoutReady) return;
        presetLbl.Location = new Point(Scale(12), Scale(7));
    }

    private void BuildBody()
    {
        // 上半区：参数配置流（FlowLayoutPanel）
        var flowContainer = new Panel
        {
            Dock = DockStyle.Fill,
            Padding = new Padding(Scale(6), 0, Scale(6), 0)
        };
        var flowTitle = new Label
        {
            Text = "转码参数：",
            Dock = DockStyle.Top,
            Height = Scale(24),
            Padding = new Padding(Scale(8), Scale(4), 0, 0),
            Font = new Font(Font, FontStyle.Bold),
            ForeColor = Color.FromArgb(70, 70, 70)
        };
        flowContainer.Controls.Add(flowPanel);
        flowContainer.Controls.Add(flowTitle);
        splitBody.Panel1.Controls.Add(flowContainer);

        // 下半区：参数用法说明（可滚动的多行文本框）
        var descContainer = new Panel
        {
            Dock = DockStyle.Fill,
            Padding = new Padding(Scale(10), 0, Scale(10), Scale(6))
        };
        var descTitle = new Label
        {
            Text = "参数用法说明：",
            Dock = DockStyle.Top,
            Height = Scale(24),
            Padding = new Padding(Scale(2), Scale(4), 0, 0),
            Font = new Font(Font, FontStyle.Bold),
            ForeColor = Color.FromArgb(70, 70, 70)
        };
        descContainer.Controls.Add(descBox);
        descContainer.Controls.Add(descTitle);
        splitBody.Panel2.Controls.Add(descContainer);

        Controls.Add(splitBody);
    }

    private void BuildFooter()
    {
        footerPanel.Dock = DockStyle.Bottom;
        footerPanel.Height = Scale(138);
        footerPanel.Resize += (_, _) => ReflowFooter();

        trialBox.ReadOnly = true;
        trialBox.Font = new Font("Consolas", 9f);
        trialBox.BackColor = Color.White;
        trialBox.BorderStyle = BorderStyle.FixedSingle;

        previewBox.ReadOnly = true;
        previewBox.Font = new Font("Consolas", 9f);
        previewBox.BackColor = Color.FromArgb(245, 245, 245);
        previewBox.BorderStyle = BorderStyle.FixedSingle;

        statusLabel.AutoEllipsis = true;
        statusLabel.ForeColor = Color.Gray;

        btnReset.Text = "清空全部";
        btnReset.Click += (_, _) =>
        {
            values.Clear();
            RebuildFlowAndDocs();
            UpdateTrial();
        };

        btnCopy.Text = "复制参数串";
        btnCopy.Click += (_, _) =>
        {
            if (!string.IsNullOrEmpty(previewBox.Text))
            {
                Clipboard.SetText(previewBox.Text);
            }
        };

        btnEditMode.Text = "手动编辑文本";
        btnEditMode.Click += (_, _) => ToggleEditMode();

        btnCancel.Text = "取消";
        btnCancel.DialogResult = DialogResult.Cancel;

        btnSave.Text = "保存";
        btnSave.BackColor = Color.FromArgb(11, 83, 148);
        btnSave.ForeColor = Color.White;
        btnSave.FlatStyle = FlatStyle.Flat;
        btnSave.FlatAppearance.BorderSize = 0;
        btnSave.Font = new Font(Font, FontStyle.Bold);
        btnSave.Click += (_, _) => OnSave();

        CancelButton = btnCancel;
        AcceptButton = btnSave;

        footerPanel.Controls.AddRange(new Control[]
        {
            lblTrial, trialBox, lblPreview, previewBox, statusLabel,
            btnReset, btnCopy, btnEditMode, btnCancel, btnSave
        });
        Controls.Add(footerPanel);
    }

    private void ReflowFooter()
    {
        if (!layoutReady || reflowingFooter) return;
        reflowingFooter = true;
        try
        {
            var pad = Scale(12);
            var padBottom = Scale(6);
            var btnH = Scale(28);
            var btnTop = Scale(102);
            var targetHeight = btnTop + btnH + padBottom;
            if (footerPanel.Height != targetHeight)
            {
                footerPanel.Height = targetHeight;
            }

            var width = footerPanel.ClientSize.Width - pad * 2;

            lblTrial.Location = new Point(pad, Scale(6));
            trialBox.SetBounds(pad, Scale(24), width, Scale(24));

            lblPreview.Location = new Point(pad, Scale(52));
            statusLabel.Location = new Point(lblPreview.Right + Scale(8), Scale(52));
            previewBox.SetBounds(pad, Scale(70), width, Scale(24));

            var btnSaveW = Scale(100);
            var btnCancelW = Scale(80);
            btnSave.SetBounds(footerPanel.ClientSize.Width - pad - btnSaveW, btnTop, btnSaveW, btnH);
            btnCancel.SetBounds(btnSave.Left - Scale(8) - btnCancelW, btnTop, btnCancelW, btnH);

            btnReset.SetBounds(pad, btnTop, Scale(90), btnH);
            btnCopy.SetBounds(btnReset.Right + Scale(6), btnTop, Scale(100), btnH);
            btnEditMode.SetBounds(btnCopy.Right + Scale(6), btnTop, Scale(110), btnH);
        }
        finally
        {
            reflowingFooter = false;
        }
    }

    private void RebuildFlowAndDocs()
    {
        flowPanel.SuspendLayout();
        flowPanel.Controls.Clear();

        var descLines = new List<string>();

        foreach (var opt in CliOptions.All)
        {
            if (!opt.Supported || opt.ShadowedBy != null) continue;

            flowPanel.Controls.Add(BuildChip(opt));

            var aliasText = (opt.Aliases != null && opt.Aliases.Length > 0)
                ? $"（别名 {string.Join(" / ", opt.Aliases)}）"
                : "";
            descLines.Add($"--{opt.Name}：{opt.Description}{aliasText}");
        }

        descBox.Text = string.Join(Environment.NewLine, descLines);
        flowPanel.ResumeLayout();
        UpdatePreview();
    }

    private Control BuildChip(CliOption opt)
    {
        // 每一个 Chip 采用小型 FlowLayoutPanel（WrapContents=false），确保标签、输入框和分隔符作为一个整体换行
        var chip = new FlowLayoutPanel
        {
            Name = "chip_" + opt.Name,
            Tag = opt,
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            FlowDirection = FlowDirection.LeftToRight,
            WrapContents = false,
            Margin = new Padding(Scale(2), Scale(3), Scale(2), Scale(3)),
            Padding = new Padding(0)
        };

        var tooltipText = opt.Description + (opt.Aliases != null && opt.Aliases.Length > 0
            ? "（别名 " + string.Join(" / ", opt.Aliases) + "）"
            : "");

        if (opt.Kind == CliValueKind.Flag)
        {
            var raw = values.TryGetValue(opt.Name, out var v) ? v : null;
            var chk = new CheckBox
            {
                Name = "chk_" + opt.Name,
                Text = "--" + opt.Name,
                AutoSize = true,
                Checked = IsTruthy(raw),
                ForeColor = Color.FromArgb(48, 48, 48),
                Margin = new Padding(0, Scale(3), Scale(2), 0)
            };
            chk.CheckedChanged += (_, _) => OnValueChanged(opt.Name, chk.Checked ? "true" : null);
            tip.SetToolTip(chk, tooltipText);
            chip.Controls.Add(chk);
        }
        else
        {
            var lbl = new Label
            {
                Name = "lbl_" + opt.Name,
                Text = "--" + opt.Name,
                AutoSize = true,
                TextAlign = ContentAlignment.MiddleLeft,
                ForeColor = Color.FromArgb(48, 48, 48),
                Margin = new Padding(0, Scale(4), Scale(2), 0)
            };
            var input = BuildInput(opt);
            tip.SetToolTip(lbl, tooltipText);
            tip.SetToolTip(input, tooltipText);
            chip.Controls.AddRange(new Control[] { lbl, input });
        }

        var sep = new Label
        {
            Text = "|",
            AutoSize = true,
            ForeColor = Color.FromArgb(180, 180, 180),
            Margin = new Padding(Scale(4), Scale(4), Scale(4), 0)
        };
        chip.Controls.Add(sep);

        return chip;
    }

    private Control BuildInput(CliOption opt)
    {
        var raw = values.TryGetValue(opt.Name, out var v) ? v : null;
        switch (opt.Kind)
        {
            case CliValueKind.Choice:
            {
                var combo = new ComboBox
                {
                    Name = "cmb_" + opt.Name,
                    DropDownStyle = ComboBoxStyle.DropDownList,
                    Width = Scale(85),
                    Margin = new Padding(0, Scale(1), 0, 0)
                };
                combo.Items.Add("");
                if (opt.Choices != null) combo.Items.AddRange(opt.Choices);
                combo.SelectedItem = raw ?? "";
                combo.SelectedIndexChanged += (_, _) => OnValueChanged(opt.Name, combo.SelectedItem as string);
                return combo;
            }
            default:
            {
                var width = InputWidth(opt);
                var box = new TextBox
                {
                    Name = "txt_" + opt.Name,
                    Text = raw ?? "",
                    Width = Scale(width),
                    Margin = new Padding(0, Scale(1), 0, 0)
                };
                box.TextChanged += (_, _) => OnValueChanged(opt.Name, box.Text);
                return box;
            }
        }
    }

    private static int InputWidth(CliOption opt)
    {
        return opt.Name switch
        {
            "fps" or "speed" or "start" or "count" => 42,
            "video-quality" or "audio-quality" => 42,
            "dimension" => 54,
            "video-bitrate" or "audio-bitrate" => 60,
            "video-codec" or "audio-codec" => 90,
            "prefix" or "suffix" or "include" or "exclude" or "regex" => 75,
            "metadata" or "ffargs" => 120,
            _ => opt.Kind switch
            {
                CliValueKind.Number => 42,
                CliValueKind.Bitrate => 60,
                _ => 75
            }
        };
    }

    private static bool IsTruthy(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return false;
        return !string.Equals(raw, "false", StringComparison.OrdinalIgnoreCase);
    }

    private void OnValueChanged(string name, string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            values.Remove(name);
        }
        else
        {
            values[name] = value;
        }

        UpdatePreview();
        trialTimer.Stop();
        trialTimer.Start();
    }

    private void ToggleEditMode()
    {
        if (!editing)
        {
            EnterEditMode();
            return;
        }

        var parsed = CliArgParser.Parse(previewBox.Text);
        if (parsed.Warnings.Count > 0)
        {
            statusLabel.ForeColor = Color.FromArgb(176, 30, 30);
            statusLabel.Text = $"（仍有 {parsed.Warnings.Count} 项参数无法解析）";
            if (layoutReady) statusLabel.Location = new Point(lblPreview.Right + Scale(8), lblPreview.Top);
            return;
        }

        editing = false;
        previewBox.ReadOnly = true;
        previewBox.BackColor = Color.FromArgb(245, 245, 245);
        btnEditMode.Text = "手动编辑文本";

        values.Clear();
        foreach (var (k, v) in ParamSerializer.Deserialize(previewBox.Text).Values)
        {
            values[k] = v;
        }
        RebuildFlowAndDocs();
        UpdateTrial();
    }

    private void EnterEditMode(string? reason = null)
    {
        editing = true;
        previewBox.ReadOnly = false;
        previewBox.BackColor = Color.White;
        btnEditMode.Text = "完成编辑";
        if (reason != null)
        {
            statusLabel.ForeColor = Color.FromArgb(176, 30, 30);
            statusLabel.Text = $"（{reason}）";
        }
        else
        {
            statusLabel.ForeColor = Color.FromArgb(11, 83, 148);
            statusLabel.Text = "（手动编辑模式）";
        }
        if (layoutReady) statusLabel.Location = new Point(lblPreview.Right + Scale(8), lblPreview.Top);
        previewBox.Focus();
        previewBox.SelectionStart = previewBox.TextLength;
    }

    public IReadOnlyList<string> ValidateAll()
    {
        var parsed = CliArgParser.Parse(previewBox.Text);
        var errors = new List<string>(parsed.Warnings);
        var ffargs = parsed.Get("ffargs");
        if (!string.IsNullOrWhiteSpace(ffargs))
        {
            errors.AddRange(FfargsValidator.Parse(ffargs).Warnings);
        }
        return errors;
    }

    private void OnSave()
    {
        var errors = ValidateAll();
        if (errors.Count > 0)
        {
            ShowValidationErrors(errors);
            return;
        }

        CliArgs = previewBox.Text;
        DialogResult = DialogResult.OK;
        Close();
    }

    protected virtual void ShowValidationErrors(IReadOnlyList<string> errors)
    {
        statusLabel.ForeColor = Color.FromArgb(176, 30, 30);
        statusLabel.Text = $"（有 {errors.Count} 项参数不合法，已阻止保存）";
        if (layoutReady) statusLabel.Location = new Point(lblPreview.Right + Scale(8), lblPreview.Top);
        MessageBox.Show(
            this,
            "以下参数不合法，已阻止保存：" + Environment.NewLine + Environment.NewLine +
            "  · " + string.Join(Environment.NewLine + "  · ", errors),
            "参数校验未通过",
            MessageBoxButtons.OK,
            MessageBoxIcon.Exclamation);
    }

    private void UpdatePreview()
    {
        if (editing) return;
        var ser = ParamSerializer.Serialize(values);
        previewBox.Text = ser.Text;
        if (ser.Skipped.Count > 0)
        {
            statusLabel.ForeColor = Color.FromArgb(166, 106, 0);
            statusLabel.Text = $"（{ser.Skipped[0]}）";
        }
        else
        {
            statusLabel.ForeColor = Color.Gray;
            statusLabel.Text = $"（生效参数数量：{values.Count}）";
        }
        if (layoutReady) statusLabel.Location = new Point(lblPreview.Right + Scale(8), lblPreview.Top);
    }

    private void UpdateTrial()
    {
        var trial = ParamTrial.Run(presetName, previewBox.Text);
        trialBox.Text = trial.Error != null ? "无法生成示例：" + trial.Error : trial.CommandLine;
        trialBox.SelectionStart = 0;
    }

    private static string FormatPresetSummary(string name)
    {
        try
        {
            TranscodeSession.EnsurePresetsLoaded();
            var p = FFmpegPresets.GetPreset(name);
            if (p == null) return $"预设：{name}";

            var parts = new List<string>();
            if (!string.IsNullOrEmpty(p.Format)) parts.Add($"格式: {p.Format.TrimStart('.')}");
            if (!string.IsNullOrEmpty(p.VideoCodecFamily)) parts.Add($"视频编码: {p.VideoCodecFamily}");
            if (p.Dimension > 0) parts.Add($"长边: {p.Dimension}");
            if (p.VideoQuality > 0) parts.Add($"质量: {p.VideoQuality:0.##}");
            if (p.VideoBitrate > 0) parts.Add($"码率: {FormatBitrate(p.VideoBitrate)}");
            else if (p.MaxBitrate > 0) parts.Add($"码率上限: {FormatBitrate(p.MaxBitrate)}");

            if (!string.IsNullOrEmpty(p.AudioCodec))
            {
                var audio = p.AudioCodec;
                if (p.AudioBitrate > 0) audio += $" {FormatBitrate(p.AudioBitrate)}";
                else if (p.AudioQuality > 0) audio += $" Q{p.AudioQuality:0.##}";
                parts.Add($"音频: {audio}");
            }
            if (p.Framerate > 0) parts.Add($"帧率: {p.Framerate:0.###}");
            if (p.Speed != 1 && p.Speed > 0) parts.Add($"变速: {p.Speed:0.##}x");

            var detail = parts.Count > 0 ? "  [" + string.Join(" | ", parts) + "]" : "";
            return $"预设：{p.Name}{detail}";
        }
        catch
        {
            return $"预设：{name}";
        }
    }

    private static string FormatBitrate(long bps)
    {
        if (bps <= 0) return "";
        if (bps % 1_000_000 == 0) return $"{bps / 1_000_000}M";
        if (bps % 1_000 == 0) return $"{bps / 1_000}k";
        return $"{bps}bps";
    }

    private int Scale(int value)
    {
        return (int)Math.Round(value * DeviceDpi / 96.0);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            trialTimer.Dispose();
            tip.Dispose();
        }
        base.Dispose(disposing);
    }
}
