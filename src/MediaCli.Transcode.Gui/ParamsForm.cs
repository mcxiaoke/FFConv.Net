using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Windows.Forms;

namespace MediaCli.Transcode.Gui;

public class ParamsForm : Form
{
	private const int RowHeight = 30;

	private const int NameWidth = 190;

	private const int DescLeft = 196;

	private const int RightPad = 8;

	private const int MinDescWidth = 80;

	private const int SidePad = 12;

	private readonly string presetName;

	private readonly Dictionary<string, string?> values = new(StringComparer.Ordinal);

	private readonly TextBox searchBox = new TextBox
	{
		Name = "paramSearchBox"
	};

	private readonly Panel scrollPanel = new Panel
	{
		Name = "paramScrollPanel"
	};

	private readonly Panel footerPanel = new Panel
	{
		Name = "footerPanel"
	};

	private readonly TextBox trialBox = new TextBox
	{
		Name = "paramTrialBox"
	};

	private readonly TextBox previewBox = new TextBox
	{
		Name = "paramPreviewBox"
	};

	private readonly Label statusLabel = new Label
	{
		Name = "paramStatusLabel"
	};

	private readonly Button btnEditMode = new Button
	{
		Name = "btnParamEditMode"
	};

	private readonly Button btnCopy = new Button
	{
		Name = "btnParamCopy"
	};

	private readonly Button btnReset = new Button
	{
		Name = "btnParamReset"
	};

	private readonly Button btnCancel = new Button
	{
		Name = "btnParamCancel"
	};

	private readonly Button btnSave = new Button
	{
		Name = "btnParamSave"
	};

	private readonly ToolTip tip = new ToolTip();

	private readonly System.Windows.Forms.Timer trialTimer = new();

	private bool editing;

	private bool hasLoadWarnings;

	private bool layoutReady;

	public string CliArgs { get; private set; } = "";

	public ParamsForm(string presetName, string cliArgs)
	{
		this.presetName = presetName;
		Text = "高级参数";
		ClientSize = new Size(940, 700);
		MinimumSize = new Size(760, 520);
		StartPosition = FormStartPosition.CenterParent;
		Font = new Font("Microsoft YaHei UI", 9f);
		DeserializeResult deserializeResult = ParamSerializer.Deserialize(cliArgs);
		foreach (KeyValuePair<string, string> value in deserializeResult.Values)
		{
			values[value.Key] = value.Value;
		}
		CliArgs = cliArgs.Trim();
		hasLoadWarnings = deserializeResult.Warnings.Count > 0;
		BuildScrollArea();
		BuildFooter();
		BuildHeader();
		previewBox.Text = CliArgs;
		if (hasLoadWarnings)
		{
			EnterEditMode($"有 {deserializeResult.Warnings.Count} 项参数无法解析，请先修正后再保存。");
		}
		RebuildRows();
		UpdateTrial();
		trialTimer.Interval = 250;
		trialTimer.Tick += (object? _, EventArgs _) =>
		{
			trialTimer.Stop();
			UpdateTrial();
		};
		Shown += (object? _, EventArgs _) =>
		{
			layoutReady = true;
			ReflowRows();
			ReflowFooter();
			ResetScroll();
		};
		AutoScaleMode = AutoScaleMode.Dpi;
		AutoScaleDimensions = new SizeF(96f, 96f);
	}

	private void BuildHeader()
	{
		Panel panel = new Panel
		{
			Name = "headerPanel",
			Dock = DockStyle.Top,
			Height = 44
		};
		Label label = new Label
		{
			Name = "paramPresetLabel",
			Text = "预设：" + presetName,
			AutoSize = true,
			Location = new Point(12, 14)
		};
		Label label2 = new Label
		{
			Text = "搜索",
			AutoSize = true,
			Location = new Point(300, 14)
		};
		searchBox.SetBounds(340, 10, 260, 24);
		searchBox.PlaceholderText = "按参数名或说明过滤";
		searchBox.TextChanged += (object? _, EventArgs _) =>
		{
			RebuildRows();
		};
		Label label3 = new Label
		{
			Text = "改动即时试算，保存前会校验全部参数",
			ForeColor = Color.Gray,
			AutoSize = true,
			Location = new Point(620, 14)
		};
		panel.Controls.AddRange(new Control[4] { label, label2, searchBox, label3 });
		Controls.Add(panel);
	}

	private void BuildScrollArea()
	{
		scrollPanel.Dock = DockStyle.Fill;
		scrollPanel.AutoScroll = true;
		scrollPanel.BackColor = Color.FromArgb(250, 250, 250);
		scrollPanel.Resize += (object? _, EventArgs _) =>
		{
			ReflowRows();
		};
		Controls.Add(scrollPanel);
	}

	private void ReflowFooter()
	{
		double s;
		if (layoutReady)
		{
			s = (double)DeviceDpi / 96.0;
			int num = S(12);
			int width = footerPanel.ClientSize.Width - num * 2;
			trialBox.SetBounds(num, S(22), width, S(24));
			previewBox.SetBounds(num, S(70), width, S(24));
			statusLabel.SetBounds(num, S(98), width, S(18));
			int num2 = S(100);
			int num3 = S(80);
			btnSave.SetBounds(footerPanel.ClientSize.Width - num - num2, S(120), num2, S(28));
			btnCancel.SetBounds(btnSave.Left - S(8) - num3, S(120), num3, S(28));
			btnReset.SetBounds(num, S(120), S(90), S(28));
			btnCopy.SetBounds(btnReset.Right + S(6), S(120), S(100), S(28));
			btnEditMode.SetBounds(btnCopy.Right + S(6), S(120), S(110), S(28));
		}
		int S(int v)
		{
			return (int)Math.Round((double)v * s);
		}
	}

	private void BuildFooter()
	{
		footerPanel.Dock = DockStyle.Bottom;
		footerPanel.Height = 152;
		footerPanel.Resize += (object? _, EventArgs _) =>
		{
			ReflowFooter();
		};
		Label label = new Label
		{
			Text = "参数试算（示意命令，非最终命令；真实命令用主界面的「预览命令」）",
			AutoSize = true,
			Location = new Point(12, 6)
		};
		trialBox.ReadOnly = true;
		trialBox.Font = new Font("Consolas", 9f);
		trialBox.BackColor = Color.White;
		trialBox.BorderStyle = BorderStyle.FixedSingle;
		Label label2 = new Label
		{
			Text = "写入参数框的文本（可直接复制到命令行）",
			AutoSize = true,
			Location = new Point(12, 54)
		};
		previewBox.ReadOnly = true;
		previewBox.Font = new Font("Consolas", 9f);
		previewBox.BackColor = Color.FromArgb(245, 245, 245);
		statusLabel.AutoEllipsis = true;
		statusLabel.ForeColor = Color.Gray;
		btnReset.Text = "清空全部";
		btnReset.Click += (object? _, EventArgs _) =>
		{
			values.Clear();
			RebuildRows();
			UpdateTrial();
		};
		btnCopy.Text = "复制参数串";
		btnCopy.Click += (object? _, EventArgs _) =>
		{
			if (!string.IsNullOrEmpty(previewBox.Text))
			{
				Clipboard.SetText(previewBox.Text);
			}
		};
		btnEditMode.Text = "手动编辑文本";
		btnEditMode.Click += (object? _, EventArgs _) =>
		{
			ToggleEditMode();
		};
		btnCancel.Text = "取消";
		btnCancel.DialogResult = DialogResult.Cancel;
		btnSave.Text = "保存";
		btnSave.BackColor = Color.FromArgb(11, 83, 148);
		btnSave.ForeColor = Color.White;
		btnSave.FlatStyle = FlatStyle.Flat;
		btnSave.FlatAppearance.BorderSize = 0;
		btnSave.Font = new Font(Font, FontStyle.Bold);
		btnSave.Click += (object? _, EventArgs _) =>
		{
			OnSave();
		};
		CancelButton = btnCancel;
		AcceptButton = btnSave;
		trialBox.SetBounds(12, 22, 916, 24);
		previewBox.SetBounds(12, 70, 916, 24);
		statusLabel.SetBounds(12, 98, 916, 18);
		btnReset.SetBounds(12, 120, 90, 28);
		btnCopy.SetBounds(108, 120, 100, 28);
		btnEditMode.SetBounds(214, 120, 110, 28);
		btnCancel.SetBounds(712, 120, 80, 28);
		btnSave.SetBounds(798, 120, 100, 28);
		footerPanel.Controls.AddRange(new Control[10] { label, trialBox, label2, previewBox, statusLabel, btnReset, btnCopy, btnEditMode, btnCancel, btnSave });
		Controls.Add(footerPanel);
	}

	private void RebuildRows()
	{
		scrollPanel.SuspendLayout();
		scrollPanel.Controls.Clear();
		string text = searchBox.Text.Trim();
		int num = 0;
		foreach (CliOption item in CliOptions.All)
		{
			if (item.Supported && item.ShadowedBy == null && (text.Length <= 0 || Matches(item, text)))
			{
				scrollPanel.Controls.Add(BuildRow(item, num++));
			}
		}
		if (num == 0)
		{
			scrollPanel.Controls.Add(new Label
			{
				Text = "没有匹配的参数。",
				ForeColor = Color.Gray,
				AutoSize = true,
				Location = new Point(12, 10)
			});
		}
		scrollPanel.ResumeLayout();
		ReflowRows();
		ResetScroll();
		UpdatePreview();
	}

	private static bool Matches(CliOption opt, string query)
	{
		if (!opt.Name.Contains(query, StringComparison.OrdinalIgnoreCase) && !opt.Description.Contains(query, StringComparison.OrdinalIgnoreCase))
		{
			return opt.Aliases?.Any((string a) => a.Contains(query, StringComparison.OrdinalIgnoreCase)) ?? false;
		}
		return true;
	}

	private Control BuildRow(CliOption opt, int index)
	{
		Panel panel = new Panel
		{
			Name = "row_" + opt.Name,
			Tag = opt,
			Left = 12,
			Top = index * 30,
			Height = 30,
			BackColor = Color.White
		};
		Label label = new Label
		{
			Name = "lbl_" + opt.Name,
			Text = "--" + opt.Name,
			AutoSize = false,
			TextAlign = ContentAlignment.MiddleLeft,
			ForeColor = Color.FromArgb(48, 48, 48)
		};
		label.SetBounds(0, 4, 190, 22);
		string text = opt.Description;
		string[]? aliases = opt.Aliases;
		if (aliases != null && aliases.Length > 0)
		{
			text = text + "（别名 " + string.Join(" / ", aliases) + "）";
		}
		Label label2 = new Label
		{
			Name = "desc_" + opt.Name,
			Text = text,
			AutoSize = false,
			AutoEllipsis = true,
			TextAlign = ContentAlignment.MiddleLeft,
			ForeColor = Color.FromArgb(96, 96, 96)
		};
		label2.SetBounds(196, 4, 80, 22);
		Control control = BuildInput(opt);
		panel.Controls.AddRange(new Control[3] { label, label2, control });
		tip.SetToolTip(label2, text);
		return panel;
	}

	private void ReflowRows()
	{
		if (!layoutReady)
		{
			return;
		}
		int num = Scale(12);
		int num2 = Scale(30);
		int num3 = scrollPanel.ClientSize.Width - num * 2;
		if (num3 < Scale(240))
		{
			num3 = Scale(240);
		}
		int num4 = 0;
		foreach (Control control3 in scrollPanel.Controls)
		{
			if (control3.Tag is CliOption opt)
			{
				control3.SetBounds(num, num4 * num2, num3, num2);
				num4++;
				control3.Controls[0].SetBounds(0, Scale(4), Scale(190), Scale(22));
				Control control2 = control3.Controls[2];
				int num5 = Scale(InputWidth(opt));
				control2.SetBounds(num3 - num5 - Scale(8), Scale(3), num5, Scale(24));
				control3.Controls[1].SetBounds(width: Math.Max(Scale(80), control2.Left - Scale(196) - Scale(8)), x: Scale(196), y: Scale(4), height: Scale(22));
			}
		}
	}

	private int Scale(int value)
	{
		return (int)Math.Round((double)(value * DeviceDpi) / 96.0);
	}

	private static int InputWidth(CliOption opt)
	{
		return opt.Kind switch
		{
			CliValueKind.Flag => 24, 
			CliValueKind.Choice => 160, 
			CliValueKind.Text => 300, 
			_ => 140, 
		};
	}

	private Control BuildInput(CliOption opt)
	{
		string? text = (values.TryGetValue(opt.Name, out string? value) ? value : null);
		switch (opt.Kind)
		{
		case CliValueKind.Flag:
		{
			CheckBox chk = new CheckBox
			{
				Name = "chk_" + opt.Name,
				Text = "",
				Checked = IsTruthy(text)
			};
			chk.CheckedChanged += (object? _, EventArgs _) =>
			{
				OnValueChanged(opt.Name, chk.Checked ? "true" : null);
			};
			return chk;
		}
		case CliValueKind.Choice:
		{
			ComboBox combo = new ComboBox
			{
				Name = "cmb_" + opt.Name,
				DropDownStyle = ComboBoxStyle.DropDownList
			};
			combo.Items.Add("");
			ComboBox.ObjectCollection items = combo.Items;
			object[] items2 = opt.Choices ?? Array.Empty<string>();
			items.AddRange(items2);
			combo.SelectedItem = text ?? "";
			combo.SelectedIndexChanged += (object? _, EventArgs _) =>
			{
				OnValueChanged(opt.Name, combo.SelectedItem as string);
			};
			return combo;
		}
		default:
		{
			TextBox box = new TextBox
			{
				Name = "txt_" + opt.Name,
				Text = (text ?? "")
			};
			box.TextChanged += (object? _, EventArgs _) =>
			{
				OnValueChanged(opt.Name, box.Text);
			};
			return box;
		}
		}
	}

	private static bool IsTruthy(string? raw)
	{
		if (!string.IsNullOrWhiteSpace(raw))
		{
			return !string.Equals(raw, "false", StringComparison.OrdinalIgnoreCase);
		}
		return false;
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

	private void ResetScroll()
	{
		scrollPanel.AutoScrollPosition = new Point(0, 0);
	}

	private void ToggleEditMode()
	{
		if (!editing)
		{
			EnterEditMode();
			return;
		}
		CliArgsParseResult cliArgsParseResult = CliArgParser.Parse(previewBox.Text);
		if (cliArgsParseResult.Warnings.Count > 0)
		{
			statusLabel.ForeColor = Color.FromArgb(176, 30, 30);
			statusLabel.Text = $"仍有 {cliArgsParseResult.Warnings.Count} 项参数无法解析，请修正后再完成编辑。";
			return;
		}
		editing = false;
		previewBox.ReadOnly = true;
		previewBox.BackColor = Color.FromArgb(245, 245, 245);
		btnEditMode.Text = "手动编辑文本";
		values.Clear();
		foreach (KeyValuePair<string, string> value in ParamSerializer.Deserialize(previewBox.Text).Values)
		{
			values[value.Key] = value.Value;
		}
		RebuildRows();
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
			statusLabel.Text = reason;
		}
		previewBox.Focus();
		previewBox.SelectionStart = previewBox.TextLength;
	}

	public IReadOnlyList<string> ValidateAll()
	{
		CliArgsParseResult cliArgsParseResult = CliArgParser.Parse(previewBox.Text);
		List<string> list = new List<string>(cliArgsParseResult.Warnings);
		string? text = cliArgsParseResult.Get("ffargs");
		if (!string.IsNullOrWhiteSpace(text))
		{
			list.AddRange(FfargsValidator.Parse(text).Warnings);
		}
		return list;
	}

	private void OnSave()
	{
		IReadOnlyList<string> readOnlyList = ValidateAll();
		if (readOnlyList.Count > 0)
		{
			ShowValidationErrors(readOnlyList);
			return;
		}
		CliArgs = previewBox.Text;
		DialogResult = DialogResult.OK;
		Close();
	}

	protected virtual void ShowValidationErrors(IReadOnlyList<string> errors)
	{
		statusLabel.ForeColor = Color.FromArgb(176, 30, 30);
		statusLabel.Text = $"有 {errors.Count} 项参数不合法，已阻止保存。";
		MessageBox.Show(this, "以下参数不合法，已阻止保存：" + Environment.NewLine + Environment.NewLine + "  · " + string.Join(Environment.NewLine + "  · ", errors), "参数校验未通过", MessageBoxButtons.OK, MessageBoxIcon.Exclamation);
	}

	private void UpdatePreview()
	{
		if (!editing)
		{
			SerializeResult serializeResult = ParamSerializer.Serialize(values);
			previewBox.Text = serializeResult.Text;
			if (serializeResult.Skipped.Count > 0)
			{
				statusLabel.ForeColor = Color.FromArgb(166, 106, 0);
				statusLabel.Text = serializeResult.Skipped[0];
				return;
			}
			statusLabel.ForeColor = Color.Gray;
			statusLabel.Text = $"参数数量 {values.Count}";
		}
	}

	private void UpdateTrial()
	{
		ParamTrialResult paramTrialResult = ParamTrial.Run(presetName, previewBox.Text);
		trialBox.Text = ((paramTrialResult.Error != null) ? ("无法试算：" + paramTrialResult.Error) : paramTrialResult.CommandLine);
		trialBox.SelectionStart = 0;
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
