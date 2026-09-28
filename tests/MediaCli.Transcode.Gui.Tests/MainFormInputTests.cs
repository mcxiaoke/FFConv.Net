using MediaCli.Transcode.Gui;
using Xunit;

namespace MediaCli.Transcode.Gui.Tests;

/// <summary>
/// 控件存在性、默认值与布局健壮性。
///
/// 这些用例的价值在于：布局是纯代码写的（无设计器），一旦改动把控件漏加进
/// Controls、Name 写错、或把控件摆到可视区域外，这里会立刻失败——
/// 而不是等用户打开界面才发现按钮不见了。
/// </summary>
public class MainFormLayoutTests
{
    /// <summary>所有应当存在且可交互的控件名。</summary>
    public static TheoryData<string, Type> ExpectedControls() => new()
    {
        { "inputBox", typeof(TextBox) },
        { "btnFile", typeof(Button) },
        { "btnDir", typeof(Button) },
        { "presetCombo", typeof(ComboBox) },
        { "hwaccelCombo", typeof(ComboBox) },
        { "decodeCombo", typeof(ComboBox) },
        { "btnAbout", typeof(Button) },
        { "outputBox", typeof(TextBox) },
        { "btnOutput", typeof(Button) },
        { "modeDir", typeof(RadioButton) },
        { "modeTree", typeof(RadioButton) },
        { "modeFile", typeof(RadioButton) },
        { "cliArgsBox", typeof(TextBox) },
        { "overrideCheck", typeof(CheckBox) },
        { "strictCheck", typeof(CheckBox) },
        { "debugCheck", typeof(CheckBox) },
        { "animeCheck", typeof(CheckBox) },
        { "syncLogCheck", typeof(CheckBox) },
        { "btnPreview", typeof(Button) },
        { "btnRun", typeof(Button) },
        { "btnCancel", typeof(Button) },
        { "btnClear", typeof(Button) },
        { "btnOpenOutput", typeof(Button) },
        { "logBox", typeof(RichTextBox) },
        { "progressBar", typeof(ProgressBar) },
        { "progressLabel", typeof(Label) },
    };

    /// <summary>
    /// 状态栏项（需求 4）。
    ///
    /// 单独列出：<see cref="ToolStripStatusLabel"/> 继承自 ToolStripItem 而非 Control，
    /// 不在 Controls 树里，因此不能用控件查找的方式断言。
    /// </summary>
    public static TheoryData<string> ExpectedStatusItems() => new()
    {
        "systemInfoLabel",
        "collectLabel",
        "stateLabel",
    };

    [Theory]
    [MemberData(nameof(ExpectedStatusItems))]
    public void StatusBarItem_Exists(string name)
    {
        Ui.RunWithForm(form =>
        {
            var item = Ui.FindToolStripItem(form, name);
            Assert.True(item is not null, $"状态栏项缺失: {name}");
            Assert.False(string.IsNullOrWhiteSpace(item!.Text), $"状态栏项 {name} 文本为空");
        });
    }

    /// <summary>状态栏显示 CPU / ffmpeg 版本信息（GPU 移至日志区，不占状态栏）。</summary>
    [Fact]
    public void StatusBar_ShowsSystemInfo()
    {
        Ui.RunWithForm(form =>
        {
            var item = Ui.FindToolStripItem(form, "systemInfoLabel");
            Assert.True(item is not null, "状态栏缺少 systemInfoLabel");

            // 探测在后台进行，等它填充
            Assert.True(Ui.WaitUntil(
                    () => Ui.ItemText(item).Contains("CPU") || Ui.ItemText(item).Contains("ffmpeg"),
                    60_000),
                $"状态栏未显示机器信息，当前为「{Ui.ItemText(item)}」");

            Assert.DoesNotContain("GPU", Ui.ItemText(item));
            Assert.Contains("ffmpeg", Ui.ItemText(item));
            Assert.Contains("硬件环境信息", Ui.Require<RichTextBox>(form, "logBox").Text);
        });
    }

    [Theory]
    [MemberData(nameof(ExpectedControls))]
    public void Control_Exists_WithExpectedType(string name, Type expected)
    {
        Ui.RunInSta(() =>
        {
            using var form = new TestableMainForm();
            var control = Ui.Find(form, name);
            Assert.True(control is not null, $"控件缺失: {name}");
            Assert.IsType(expected, control);
        });
    }

    /// <summary>控件必须真的挂在控件树上（不是漏加进 Controls 的孤儿）。</summary>
    [Fact]
    public void AllExpectedControls_AreInControlTree()
    {
        Ui.RunInSta(() =>
        {
            using var form = new TestableMainForm();
            foreach (var row in ExpectedControls())
            {
                var name = (string)row[0];
                var found = form.Controls.Find(name, searchAllChildren: true);
                Assert.True(found.Length == 1,
                    $"控件 {name} 在控件树中出现 {found.Length} 次，应为 1 次");
            }
        });
    }

    /// <summary>每个控件的 Name 必须唯一——重复会让按名查找变得不确定。</summary>
    [Fact]
    public void ControlNames_AreUnique()
    {
        Ui.RunInSta(() =>
        {
            using var form = new TestableMainForm();
            var names = Ui.Walk(form)
                .Select(p => p.Child.Name)
                .Where(n => !string.IsNullOrEmpty(n))
                .ToList();

            var dupes = names.GroupBy(n => n).Where(g => g.Count() > 1)
                .Select(g => $"{g.Key} x{g.Count()}").ToList();
            Assert.True(dupes.Count == 0, "控件 Name 重复: " + string.Join(", ", dupes));
        });
    }

    /// <summary>
    /// 控件必须落在父容器可视区域内。这类问题在代码布局里很常见：
    /// 容器高度写小了，最后一行控件就被裁掉，而编译与运行都不报错。
    /// </summary>
    [Fact]
    public void Controls_FitInsideTheirParent()
    {
        Ui.RunInSta(() =>
        {
            using var form = new TestableMainForm();
            form.CreateControl();
            var offenders = new List<string>();

            foreach (var (child, parent) in Ui.Walk(form))
            {
                // 跳过运行时由系统调整尺寸/位置的控件
                if (child is VScrollBar or HScrollBar) continue;
                if (parent is TextBoxBase or ComboBox or RichTextBox) continue;

                if (child.Right > parent.ClientSize.Width || child.Bottom > parent.ClientSize.Height)
                {
                    offenders.Add(
                        $"{child.Name} ({child.GetType().Name}) 越界: " +
                        $"right={child.Right} bottom={child.Bottom} " +
                        $"parent={parent.ClientSize.Width}x{parent.ClientSize.Height}");
                }
            }

            Assert.True(offenders.Count == 0,
                "控件超出父容器:" + Environment.NewLine + string.Join(Environment.NewLine, offenders));
        });
    }

    /// <summary>同一容器内的同级控件不应相互重叠（会互相遮挡）。</summary>
    [Fact]
    public void SiblingControls_DoNotOverlap()
    {
        Ui.RunInSta(() =>
        {
            using var form = new TestableMainForm();
            form.CreateControl();
            // 必须 Show()：标签是 AutoSize 的，其宽度由字体决定，而字体要到窗体句柄
            // 创建后才按 DPI 缩放比放大（本机实测 150%）。只调 CreateControl() 的话
            // 标签仍是 96 DPI 宽度，高 DPI 下的真实重叠（如「自定义参数」压到回显框上）
            // 就被测不出来——这正是本用例历史上漏掉该缺陷的原因。
            form.Show();
            Ui.Pump(200);
            var offenders = new List<string>();

            // 所有容器 = 窗体自身 + 任意带子控件的控件
            var containers = new List<Control> { form };
            containers.AddRange(Ui.Walk(form).Select(p => p.Child).Where(c => c.Controls.Count > 0));

            foreach (var container in containers)
            {
                var siblings = container.Controls.Cast<Control>()
                    .Where(c => c.Visible && c.Width > 0 && c.Height > 0)
                    // 仅排除滚动条（由容器自动管理，本就覆盖内容区）
                    .Where(c => c is not (VScrollBar or HScrollBar))
                    .ToList();

                for (var i = 0; i < siblings.Count; i++)
                {
                    for (var j = i + 1; j < siblings.Count; j++)
                    {
                        if (siblings[i].Bounds.IntersectsWith(siblings[j].Bounds))
                        {
                            offenders.Add(
                                $"[{container.Name}] {siblings[i].Name} 与 {siblings[j].Name} 重叠");
                        }
                    }
                }
            }

            Assert.True(offenders.Count == 0,
                "同级控件重叠:" + Environment.NewLine + string.Join(Environment.NewLine, offenders.Distinct()));
        });
    }

    /// <summary>
    /// AutoSize 标签与其右侧控件之间必须留有可见间距。
    ///
    /// 回归：标签位置曾用 96 DPI 的像素常量硬编码（如「自定义参数」预留 66px），
    /// 而 150% DPI 下该标签实际宽 100px，标签右边缘越过控件左边缘 1px，
    /// 表现为"文字紧贴/压住输入框边框"。间距判据比"是否重叠"更早暴露这类问题。
    /// </summary>
    [Fact]
    public void AutoSizeLabels_KeepGapFromAdjacentControls()
    {
        Ui.RunInSta(() =>
        {
            using var form = new TestableMainForm();
            form.Show();
            Ui.Pump(200);

            var offenders = new List<string>();
            foreach (var lbl in Ui.Walk(form).Select(p => p.Child).OfType<Label>()
                         .Where(l => l.AutoSize && l.Text.Length > 0))
            {
                var parent = lbl.Parent;
                if (parent is null) continue;
                var midY = lbl.Top + lbl.Height / 2;

                foreach (Control sib in parent.Controls)
                {
                    if (ReferenceEquals(sib, lbl) || sib is Label) continue;
                    var sibMidY = sib.Top + sib.Height / 2;
                    // 仅比较同一水平带上的兄弟控件
                    if (Math.Abs(sibMidY - midY) > Math.Max(lbl.Height, sib.Height) / 2) continue;
                    if (sib.Left < lbl.Left) continue;

                    var gap = sib.Left - lbl.Right;
                    if (gap < 8)
                    {
                        offenders.Add(
                            $"[{parent.Name}] 标签\"{lbl.Text}\"右边缘={lbl.Right} → {sib.Name}左边缘={sib.Left}，间距={gap}px");
                    }
                }
            }

            Assert.True(offenders.Count == 0,
                "标签与相邻控件间距过窄（高 DPI 下会贴住/重叠）:" + Environment.NewLine +
                string.Join(Environment.NewLine, offenders.Distinct()));
        });
    }

    /// <summary>
    /// 参数区第一行的三个下拉框与「使用说明」按钮必须依次排列、互不重叠。
    ///
    /// 回归：按标签真实宽度重新定位后，若宽度仍用"延伸到右侧按钮"的公式，
    /// 三个下拉框会被拉宽并挤到窗口之外（实测 decodeCombo.Right=1672 越出客户区）。
    /// </summary>
    [Fact]
    public void ParamRow_ControlsStayOrderedAndInsideClientArea()
    {
        Ui.RunInSta(() =>
        {
            using var form = new TestableMainForm();
            form.Show();
            Ui.Pump(200);

            var preset = Ui.Require<ComboBox>(form, "presetCombo");
            var hw = Ui.Require<ComboBox>(form, "hwaccelCombo");
            var dec = Ui.Require<ComboBox>(form, "decodeCombo");
            var about = Ui.Require<Button>(form, "btnAbout");
            var args = Ui.Require<TextBox>(form, "cliArgsBox");
            var paramsBtn = Ui.Require<Button>(form, "btnParams");

            Assert.True(preset.Right <= hw.Left, "presetCombo 与 hwaccelCombo 重叠");
            Assert.True(hw.Right <= dec.Left, "hwaccelCombo 与 decodeCombo 重叠");
            Assert.True(dec.Right <= about.Left, "decodeCombo 与「使用说明」重叠");
            Assert.True(args.Right <= paramsBtn.Left, "自定义参数框与「高级参数」重叠");

            // 全部控件必须落在参数 GroupBox 的客户区内
            var group = preset.Parent!;
            foreach (var c in new Control[] { preset, hw, dec, about, args, paramsBtn })
            {
                Assert.True(c.Right <= group.ClientSize.Width,
                    $"{c.Name}.Right={c.Right} 越出容器宽度 {group.ClientSize.Width}");
                Assert.True(c.Left >= 0, $"{c.Name}.Left={c.Left} 为负");
            }
        });
    }

    [Fact]
    public void Form_HasReasonableMinimumSize()
    {
        Ui.RunInSta(() =>
        {
            using var form = new TestableMainForm();
            Assert.True(form.MinimumSize.Width >= 600, "最小宽度过小，布局会被压坏");
            Assert.True(form.MinimumSize.Height >= 480, "最小高度过小，布局会被压坏");
            Assert.False(string.IsNullOrWhiteSpace(form.Text));
        });
    }

    /// <summary>
    /// 单行文本必须完整可见，不能被容器或自身宽度裁掉。
    ///
    /// 这条用例补的是两个真实盲区：
    /// 1. 原先只校验控件<b>边界</b>，于是"控件大小合规、但文字被截断"能一路通过——
    ///    实际界面里参数区的说明就断过字。
    /// 2. 复选框/单选按钮的文字渲染在<b>字形之后</b>，可用宽度要减去字形宽度。
    ///    实测 150% 缩放下复选框宽 86px、文字 72px 看似够用，但字形另占约 20px，
    ///    末字被裁——这正是 DPI 缺陷的现场表现。
    /// </summary>
    [Fact]
    public void SingleLineText_IsNotClipped()
    {
        Ui.RunWithForm(form =>
        {
            var offenders = new List<string>();

            foreach (var (control, _) in Ui.Walk(form))
            {
                if (control is not Label and not CheckBox and not RadioButton) continue;
                if (string.IsNullOrEmpty(control.Text)) continue;
                // 多行标签由系统换行，不适用单行裁切判定
                if (control.Text.Contains('\n')) continue;

                var measured = TextRenderer.MeasureText(
                    control.Text, control.Font, new Size(int.MaxValue, int.MaxValue),
                    TextFormatFlags.NoPadding | TextFormatFlags.SingleLine);

                // 复选框/单选的文字排在字形右侧，需扣除字形占宽
                var glyph = control is CheckBox or RadioButton
                    ? Ui.GlyphWidth(control)
                    : 0;

                var available = control.AutoSize
                    ? control.Parent?.ClientSize.Width ?? control.Width
                    : control.Width;

                var needed = measured.Width + glyph;
                if (needed > available)
                {
                    offenders.Add(
                        $"[{control.Name}] 需要 {needed}px（文字 {measured.Width} + 字形 {glyph}）" +
                        $"超过可用 {available}px: \"{Truncate(control.Text, 40)}\"");
                }
            }

            Assert.True(offenders.Count == 0,
                "文本被裁切:" + Environment.NewLine + string.Join(Environment.NewLine, offenders));
        });
    }

    /// <summary>
    /// 窗体必须显式声明 DPI 缩放基准。
    ///
    /// 纯代码创建的 Form 默认 AutoScaleMode.Inherit 且基准为 (0,0)，即完全不缩放。
    /// 在 >96 DPI 的屏幕上字体会放大而控件尺寸不变，固定布局被挤爆。
    /// 实测本机 4K@150% 时按钮与复选框文字被裁；此用例锁定该回归。
    ///
    /// 注意不要断言 AutoScaleDimensions == 96：自动缩放执行后 WinForms 会把它
    /// 同步为 CurrentAutoScaleDimensions（避免重复缩放），此时读到当前 DPI 是正确行为。
    /// 真正要守的是"模式为 Dpi"与"基准非零"这两点——默认值恰好都不满足。
    /// 实际缩放效果由 DpiScaling_ProducesConsistentLayout 验证。
    /// </summary>
    [Fact]
    public void Form_DeclaresDpiScalingBasis()
    {
        Ui.RunInSta(() =>
        {
            using var form = new TestableMainForm();

            Assert.Equal(AutoScaleMode.Dpi, form.AutoScaleMode);
            Assert.NotEqual(0F, form.AutoScaleDimensions.Width);
            Assert.NotEqual(0F, form.AutoScaleDimensions.Height);
        });
    }

    /// <summary>
    /// 缩放基准必须与设计基准一致，否则加载时会再次缩放导致布局漂移。
    /// 这里同时记录当前环境的缩放比，便于排查高 DPI 相关问题。
    /// </summary>
    [Fact]
    public void DpiScaling_ProducesConsistentLayout()
    {
        Ui.RunWithForm(form =>
        {
            var scale = Ui.CurrentScaleFactor();

            // 客户区应按缩放比放大（基准 96 → 当前 DPI）
            var expectedWidth = (int)Math.Round(1000 * scale);
            Assert.True(Math.Abs(form.ClientSize.Width - expectedWidth) <= 2,
                $"客户区宽度 {form.ClientSize.Width} 与期望 {expectedWidth} 不符（缩放 {scale:F2}）");

            // 控件尺寸同样应按比例放大
            var check = Ui.Require<CheckBox>(form, "overrideCheck");
            var expectedCheckWidth = (int)Math.Round(86 * scale);
            Assert.True(Math.Abs(check.Width - expectedCheckWidth) <= 2,
                $"复选框宽度 {check.Width} 与期望 {expectedCheckWidth} 不符（缩放 {scale:F2}）");
        });
    }

    /// <summary>
    /// 说明性文本在窗体最小尺寸下也必须完整可见。
    /// 用户把窗口缩到最小时仍应能读到操作提示。
    /// </summary>
    [Fact]
    public void HintText_RemainsVisibleAtMinimumSize()
    {
        Ui.RunWithForm(form =>
        {
            form.Size = form.MinimumSize;
            form.PerformLayout();
            Ui.Pump(300);

            var offenders = new List<string>();
            // cliArgsHint 已改为动态状态行 argsStatus（显示生效项数与告警数）
            foreach (var name in new[] { "inputHint", "argsStatus" })
            {
                var label = Ui.Find(form, name) as Label;
                if (label is null)
                {
                    offenders.Add($"{name} 缺失");
                    continue;
                }

                var measured = TextRenderer.MeasureText(
                    label.Text, label.Font, new Size(int.MaxValue, int.MaxValue),
                    TextFormatFlags.NoPadding | TextFormatFlags.SingleLine);

                if (measured.Width > label.Width)
                {
                    offenders.Add($"{name}: 需要 {measured.Width}px，实际 {label.Width}px");
                }
            }

            Assert.True(offenders.Count == 0,
                "最小尺寸下说明文字被裁切:" + Environment.NewLine +
                string.Join(Environment.NewLine, offenders));
        });
    }

    private static string Truncate(string s, int max) =>
        s.Length <= max ? s : s[..max] + "…";
}
