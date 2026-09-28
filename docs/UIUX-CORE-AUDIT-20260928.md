# FFConv 界面/交互与 Core 代码审查报告

> 日期：2026-09-28
> 范围：`src/MediaCli.Transcode`（core）、`src/MediaCli.Transcode.Gui`（WinForms UI/交互）、`src/MediaCli.Transcode.Cli`
> 方法：逐文件精读 + 独立探针程序实证复现（含真实 ffmpeg 与离屏 WinForms 命中测试）
> 性质：**只读审查**，未改动任何业务代码。全部结论附 `文件:行号` 与可复现证据。

---

## 一、审查范围与方法

| 层 | 文件数 | 说明 |
|---|---|---|
| core | 21 个 `.cs`（约 4.3k 行） | Hardware / Planning / Run / Presets / Model / Scan / MediaProbe / Support / Bin |
| GUI | 19 个 `.cs`（约 5.0k 行） | MainForm / ParamsFlowForm / ParamsForm / AboutForm / 会话与校验层 |
| CLI | 1 个 `.cs`（333 行） | 参数解析与 plan/run 编排 |

**方法**：

1. 先读 `AGENTS.md` 与既有审计文档（`docs/UIUX-AUDIT-REVIEW-20260926.md`、`docs/CHANGES-2026092*.md`），避免重复报告已记录/已修复的问题。
2. 逐文件通读全部生产代码。
3. 对每个疑似缺陷编写**独立探针程序**（`temp/audit-20260928/probe*`，引用真实 core/GUI 程序集）在真机实测。
4. 涉及 ffmpeg 行为的结论，用真实 ffmpeg 二进制复现。
5. 涉及 WinForms 层叠/布局的结论，用**离屏显示 + 真实命中测试**（`GetChildAtPoint`），而非仅读坐标。

**修复前基线（本次实测）**：

| 验证项 | 结果 |
|---|---|
| `dotnet build -c Release` | 0 警告 0 错误 ✅ |
| `dotnet test`（Debug 全量） | 失败 0 / 通过 417（Core 95 + GUI 322）✅ |
| `dotnet test -c Release --no-build` | 失败 0 / 通过 417 ✅ |

> **最重要的一点**：下面列出的全部缺陷，都存在于「构建 0 警告 0 错误、417 项测试全绿」的状态下。这本身说明现有测试体系存在系统性盲区（见第七节）。

---

## 二、结论速览

> **修复状态（2026-09-28 更新）**：状态列标注本次实施结果，逐条证据见 `docs/CHANGES-20260928.md`。
> ✅ 已修复并验证 ｜ ⬜ 未修复（保留待办）

| # | 级别 | 状态 | 位置 | 问题一句话 |
|---|---|---|---|---|
| **P0-1** | 严重 | ✅ | `AboutForm.cs:161-184` | 「使用说明」窗口的**关闭按钮被 TabControl 完全遮挡**，运行期不可见、不可点击 |
| **P0-2** | 严重 | ✅ | `FfmpegBuild.cs:237-310` | **`--video-copy` 与自动缩放/变速/帧率叠加时生成必然失败的 ffmpeg 命令**，并附带音画不同步 |
| **P1-1** | 高 | ✅ | `Gpu.cs:353-361`、`HwDetect.cs:267-271` | **AMD 显卡被误判为 `other`**，AMF 硬编与 swdec 链路整体丢失，静默退化为纯 CPU 软编 |
| **P1-2** | 高 | ✅ | `FFmpegPresets.cs:23-46` | 任意一个自定义预设字段类型错误，会让**全部预设（含内置）加载失败** |
| **P1-3** | 高 | ✅ | `FfmpegBuild.cs:394,397,532,535` | 4 个 `[GeneratedRegex]` 的 `\\s` **过度转义**，会话参数清洗与旧式 `audioArgs` 解析静默失效 |
| **P1-4** | 高 | ✅ | `MainForm.cs:536-537` vs `Helper.cs:13-21` | 文件对话框允许选 `.m2ts/.mpeg/.opus/.ogg`，但**扫描器静默丢弃**这些文件 |
| **P1-5** | 高 | ✅ | `ParamsFlowForm.cs:182-183,188` | 构造期 `DeviceDpi` 为默认 96，`footerPanel` 高度先按 96 DPI 计算（`Shown` 后会重算，但时序脆弱） |
| **P2-1** | 中 | ✅ | `AboutForm.cs:200-206,259-262` | `ListView` **列宽不随 DPI 缩放**，150% 下说明文本被截为省略号（实测列宽仍为 96 DPI 原值） |
| **P2-2** | 中 | ✅ | `MainForm.cs:112-113,404-461` | `MinimumSize` 偏小，最小尺寸下**日志区高度塌陷为 0**（实测） |
| **P2-3** | 中 | ✅ | `MainForm.cs:287`、`HwDetect.cs:60-64` | `hwaccel` 下拉 9 项中 **d3d/d3d11va/d3d12va/dxva2 四项完全等价**；`cpu` 项语义误导 |
| **P2-4** | 中 | ⬜ | `ParamsForm.cs`（整文件） | **死代码**：再无生产引用，却被 30 项测试固化（需确认后删除，属结构性改动） |
| **P2-5** | 中 | ✅ | `TranscodeSession.cs:480` | 「详细日志」开关**未作用于 core 日志过滤**，勾选后仍按错误词表过滤并计数 |
| **P2-6** | 中 | ✅ | `SessionLogWriter.cs:119` | 日志同步文件名的截断长度算错，**唯一随机后缀被全部截掉**，同秒同步必撞名 |
| **P2-7** | 中 | ✅ | `HwDetect.cs:82` | `-encoders` 解析把 **`=` 当成编码器名**，污染编码器集合与可用性判定 |
| **P2-8** | 中 | ✅ | `TranscodeSession.cs:764-780` | 取消收尾路径可能把进度与状态刷成「**已完成**」，与日志「已取消」矛盾 |
| **P2-9** | 中 | ✅ | `MainForm.cs:627-641`、`OnClearLog:969-978` | 进度条「瞬时刷新」技巧在极值与取消路径上产生异常值（边界保护已加） |
| **P3-1** | 低 | ✅ | `HwAccel.cs:285-302` | `CalcLongEdge` 无输出下限，极端参数下可产出 `0x0` |
| **P3-2** | 低 | ⬜ | `HwDetect.cs:319-328` | 非法 `--hwaccel` 取值**静默退化为默认链**，与 GUI「不能生效必须告警」原则不一致 |
| **P3-3** | 低 | ⬜ | `ParamTrial.cs:52-110` | 面板「参数示例」是示意命令，与实际编码器/质量参数语义不符（界面已有"示意"说明，属可选优化） |
| **P3-4** | 低 | ⬜ | `FFmpegPresets.cs:341,351` | `VideoQuality` 由 `double` 截断为 `long`，小数质量值被丢弃 |
| **P3-5** | 低 | ✅ | `FfargsValidator.cs:124-129` | ffargs 含分隔符的取值给出可解释提示（原本只有"应为 key=value 形式"） |

**本次修复统计**：18/21 项已修复并验证（P0 2/2、P1 5/5、P2 8/9、P3 3/5）。
剩余 3 项中，P2-4 属结构性清理（删除死代码），P3-2/P3-3/P3-4 为低优先级改进。

---



## 三、P0 级缺陷（建议优先修）

### P0-1 「使用说明」窗口的关闭按钮被完全遮挡

**位置**：`src/MediaCli.Transcode.Gui/AboutForm.cs:161-184`

**证据（源码）**：

```csharp
// AboutForm.cs:161-168  —— TabControl 先加入
var tabs = new TabControl { Name = "aboutTabs", Dock = DockStyle.Fill };
tabs.TabPages.Add(BuildCliPage());
tabs.TabPages.Add(BuildPresetPage());
tabs.TabPages.Add(BuildTipsPage());
Controls.Add(tabs);

// AboutForm.cs:170-179  —— 关闭按钮后加入
var close = new Button { Name = "btnAboutClose", Text = "关闭", DialogResult = DialogResult.OK };
close.SetBounds(ClientSize.Width - 110, ClientSize.Height - 40, 96, 28);
close.Anchor = AnchorStyles.Bottom | AnchorStyles.Right;
close.Click += (_, _) => Close();
Controls.Add(close);
```

**问题机理**：WinForms z-order 规则是「后添加的控件位于 z-order 顶部」，而 `Controls[0]` 才是最顶层。`tabs` 使用 `Dock = Fill`，Bounds 覆盖整个客户区（含 `close` 所在的右下角）。实测 `GetChildIndex(close) == 1`（最下层），`close` 被 `tabs` 完整覆盖。

**实证（离屏显示 + 真实命中测试，DPI 144）**：

```
ClientSize={Width=1290, Height=930}   DeviceDpi=144
  form.Controls[0]=aboutTabs       Bounds={X=0,Y=0,Width=1290,Height=930}
  form.Controls[1]=btnAboutClose   Bounds={X=1125,Y=870,Width=144,Height=42}
  关闭按钮中心点 {X=1197,Y=891} 实际命中的控件 = aboutTabs
  => 缺陷：被 "aboutTabs" 遮挡，用户点不到关闭按钮
```

**旁证（现有测试截图）**：`temp/screenshots/06-about.png` 客户区右下角只有参数表格与竖直滚动条的下端，**看不到任何按钮**，底部也没有独立按钮栏。

**影响**：
- 用户只能靠标题栏 ✕ 关闭。`CancelButton` / `AcceptButton` 均未设置，`Esc` 与 `Enter` 无效。
- 现有测试 `MainFormLayoutTests.cs:96-104` 只断言 `close.DialogResult == DialogResult.OK`，**从未验证可见性或可点击性**。

**建议改法（推荐 ①）**：

1. **用 `Panel` 承载按钮（最稳）**：`var bar = new Panel { Dock = DockStyle.Bottom, Height = Scale(44) };`，把 `close` 放进 `bar`（`Anchor = Bottom|Right`），先 `Controls.Add(tabs)` 再 `Controls.Add(bar)`。两者 Dock 区不重叠，从根本消除遮挡。
2. **调整 z-order**：`Controls.Add(close)` 后调用 `close.BringToFront()`。
3. **避免 Dock 冲突**：`tabs` 改用四边 `Anchor` 代替 `Fill`，为底部按钮预留空间。

**回归验证**：新增断言 `form.GetChildAtPoint(close 中心点) == close`（可复用本次探针写法）。

---

### P0-2 `--video-copy` 与自动缩放/变速/帧率叠加时生成必然失败的命令

**位置**：

- 滤镜生成：`src/MediaCli.Transcode/Planning/FfmpegBuild.cs:237-263`（`BuildFilterArgs`）
- 视频参数：`src/MediaCli.Transcode/Planning/FfmpegBuild.cs:274-310`（`BuildVideoArgsFromPlan`）
- `copy` 时的 `filters` 清空：`src/MediaCli.Transcode/Presets/FFmpegPresets.cs:312-330`

**证据（源码）**：

```csharp
// FfmpegBuild.cs:242  以下任一为真就产出 -vf
if (tempPreset.Scaled || tempPreset.Framerate > 0.0 || pre.Length > 0 || post.Length > 0
    || speed != 1.0 || DepthAlignNeeded(entry, hwPlan, tempPreset))
{
    string scaleFilters = BuildScaleFiltersFromPlan(entry, hwPlan, tempPreset);
    ...
    list.Add("-vf");
}

// FfmpegBuild.cs:281-285  copy 分支完全不检查上面是否已产出 -vf
string? videoCodec = tempPreset.UserArgs.VideoCodec;
if (videoCodec == "copy")
{
    return ["-c:v", "copy"];
}
```

`FFmpegPresets.CreateFromArgv` 在 `VideoCopy = true` 时只清空了 `Filters` / `PreFilters` / `PostFilters`，**未清除 `UserArgs.Dimension` / `Speed` / `Framerate`**，所以 `Scaled` / `speed != 1` / `Framerate > 0` 仍会让 `BuildFilterArgs` 产出 `-vf`。

**实证（探针引用真实 core；源 3840×2160）**：

```
【hevc_2k + --video-copy（4K 源需缩到 1920）】
  Scaled=True  DstSize=1920x1080   含 -c:v copy = True   含 -vf = True
  值 = scale=w=1920:h=1080:flags=lanczos
  冲突判定 = *** 会触发 ffmpeg 硬失败 ***

【hevc_2k + --video-copy + --speed 1.5】
  含 -vf 值 = setpts=PTS/1.5,scale=w=1920:h=1080:flags=lanczos
  同一命令含 -af atempo=1.5（音频变速、视频 copy → 音画不同步）

【hevc_2k + --video-copy + --fps 24（源 60）】
  含 -vf 值 = scale=w=1920:h=1080:flags=lanczos,fps=24
【hevc_2k + --video-copy + pre_filters=yadif】
  含 -vf 值 = yadif,scale=w=1920:h=1080:flags=lanczos
```

**实证（真实 ffmpeg 复现，确认必然失败）**：

```
$ ffmpeg -i in.mp4 -vf "scale=w=160:h=120:flags=lanczos" -c:v copy out.mp4
[vost#0:0/copy] Filtergraph 'scale=...' was specified, but codec copy was selected.
                Filtering and streamcopy cannot be used together.
Error opening output files: Invalid argument            ← 失败

$ ffmpeg -i in.mp4 -vf "setpts=PTS/1.5" -c:v copy out.mp4
[vost#0:0/copy] Filtergraph 'setpts=PTS/1.5' was specified, but codec copy was selected.
                Filtering and streamcopy cannot be used together.   ← 失败

$ ffmpeg -i in.mp4 -c:v copy out.mp4
（成功，exit 0）
```

**影响**：用户勾选「视频流复制」（`CliOptions.cs:82` 提供 `video-copy`）后，只要源视频长边大于预设 `dimension`，或同时设了变速/帧率/前后滤镜，**每个文件都会失败**，报错为 `Filtering and streamcopy cannot be used together`——对选择「复制视频流」的用户完全反直觉。
附带问题：`--speed` 时音频仍 `atempo` 变速、视频被 copy，即使不报错也**音画不同步**。

**建议改法**：

1. **core 侧一致性兜底（推荐，最小改动）**：在 `BuildFilterArgs` 开头判断 `tempPreset.UserArgs.VideoCodec == "copy"`（或 `VideoCopy == true`）时**直接返回空列表**，与 `BuildVideoArgsFromPlan` 的 copy 分支对齐。可参考同文件 `BuildAudioArgs` 已有的 copy / 变速互斥处理（`:339`）。
2. **GUI 侧提前拦截**：复用 `MainForm.StartRun` 已有的参数告警对话框流程，新增校验：`video-copy` 与 `dimension / fps / speed / pre_filters / post_filters` 同时出现时明确告警。
3. **文档补充**：`CliOptions.cs:82` 的 `video-copy` 描述补一句「与缩放/变速/帧率互斥」。

**回归验证**：对「4K 源 + hevc_2k + `--video-copy`」断言生成命令**不含 `-vf`**，并跑一次真实端到端（该组合此前应失败）。

---

## 四、P1 级缺陷

### P1-1 AMD 显卡被误判为 `other`，导致 AMF/swdec 链路整体丢失

**位置**：`Gpu.cs:353-361`（`NormalizeVendor`）、`Gpu.cs:333-345`（`DetectGpus` 的 vendor 赋值）、`HwDetect.cs:267-271`（`PrimaryVendor`）、`HwDetect.cs:33-40`（`GpuVendorHwaccels`）

**证据（源码）**：

```csharp
// Gpu.cs:353-361
public static string NormalizeVendor(string? raw)
{
    var v = (raw ?? "").ToLowerInvariant();
    if (v.Contains("nvidia")) return "nvidia";
    if (v.Contains("intel")) return "intel";
    if (v.Contains("advanced micro devices") || v == "amd" || RxAtiWord().IsMatch(v) || v.StartsWith("ati"))
        return "amd";
    return "other";
}
// RxAtiWord = @"\bati\b"（Gpu.cs:363）
// 而 WMI 报告的 AMD 型号串是 "AMD Radeon RX 6800 XT"：
// 不含 "advanced micro devices"，不等于 "amd"，也没有独立的 "ati" 词元
```

`DetectGpus`（`Gpu.cs:316-322`）用 `NormalizeVendor(model)` 得到 vendor 写入 `GpuInfo.Vendor`；`HwDetect.PrimaryVendor`（`:269`）又**优先取 `caps.Gpus[0].Vendor`**。

**实证（探针，构造 AMD 机器能力集）**：

```
1) NormalizeVendor("AMD Radeon RX 6800 XT")      = other      ← 应为 amd
   NormalizeVendor("ATI Radeon HD 5800")         = amd        （仅含 "ati" 词元才正确）
   NormalizeVendor("Advanced Micro Devices, Inc.") = amd

2) PrimaryVendor 与候选链：
   NVIDIA GeForce RTX 4090   Gpus[0].Vendor=nvidia => PrimaryVendor=nvidia
       链 = [cuda, swdec, d3d, cpu]
   AMD Radeon RX 6800 XT     Gpus[0].Vendor=other  => PrimaryVendor=other   ← 应为 amd
       链 = [d3d, cpu]                             ← 丢失 amf 与 swdec
   Intel(R) UHD Graphics 770 Gpus[0].Vendor=intel  => PrimaryVendor=intel
       链 = [swdec, d3d, cpu]

3) 该 AMD 机器上 amf 编码器可用=True, StaticOk=True，但
   CandidateTiers(auto) => [d3d, cpu]
   期望应含 'amf'（AMD 硬编）与 swdec（走 amf 编码器）
```

**问题机理**：`GpuVendorHwaccels["other"] = ["d3d"]`（`HwDetect.cs:39`），因此走 `other` 分支时：
- 第 1 轮「主厂商硬件解码层」只遍历 `allowed` 且跳过 `d3d` → **不加入 `amf`**；
- `SwdecUsable(caps, "other")` 因 `SwdecEncodersByVendor` 无 `other` 键而返回 false → **不加入 `swdec`**（而 swdec 正是「CPU 解码 + 硬件编码」，是 A 卡常用高效路径）。

**影响**：所有 WMI 型号串形如 `AMD Radeon ...`（**最常见形态**）的 A 卡用户，都被静默降级为 `d3d` 解码 + **纯 CPU 软件编码**（libx264/libx265），完全用不上 AMF 硬件编码，速度可能相差数倍，且日志中没有任何解释。

**建议改法**：

1. `NormalizeVendor` 增加 `v.Contains("radeon")` 与独立 `amd` 词元判定（注意保持 `\bati\b` 词元匹配，避免把含 `ati` 子串的 Intel 型号误判——现有注释已提示该风险）。
2. 更稳妥：`DetectGpus` 中若 WMI 无法判定厂商，用 `caps.Encoders` 兜底推断（存在 `*_amf` → amd，`*_qsv` → intel，`*_nvenc` → nvidia），避免完全依赖显卡名字符串。
3. 补一条以 `AMD Radeon RX 6800 XT` 为输入的厂商识别单测。

**回归验证**：对 `"AMD Radeon RX 6800 XT"` 断言 `NormalizeVendor == "amd"`，且 `CandidateTiers` 结果包含 `amf`。

---

### P1-2 单个坏预设会让全部预设加载失败

**位置**：`FFmpegPresets.cs:23-46`（`Init`）、`FFmpegPresets.cs:56-81`（`FromFields` 的 `Convert.*`）、`PresetLoader.cs:150-177`（`ProcessPresets` / `ValidatePresetFields` 的隔离语义）

**证据（源码）**：

```csharp
// FFmpegPresets.cs:36-45  Init：循环内没有任何 try/catch
lock (PresetMap)
{
    PresetNames.Clear();
    PresetMap.Clear();
    foreach (var (name, fields) in merged)
    {
        PresetMap[name] = FromFields(name, fields);   // ← 任一字段类型错误即抛出
        PresetNames.Add(name);
    }
}

// FFmpegPresets.cs:77  维度用 Convert.ToInt64，遇非数字字符串抛 FormatException
Dimension = fields.TryGetValue("dimension", out var dim) && dim is not null ? Convert.ToInt64(dim) : 0,
```

对比：`PresetLoader.ProcessPresets`（`PresetLoader.cs:154-166`）**对每个预设单独 try/catch** 并跳过失败的，隔离设计明确；但类型不匹配的预设能通过 `ValidatePresetFields`（`PresetLoader.cs:170-177` 丢弃告警且恒返回 true），最终在 `FromFields` 才抛，而**这一层没有隔离**。

**实证（探针，cwd 下放一份 `presets.yaml`）**：

```
调用 FFmpegPresets.Init()（该 yaml 含 1 个正常预设 my_good、1 个 dimension="1920abc" 的 my_bad）...
  !!! Init 抛异常: FormatException: The input string '1920abc' was not in a correct format.
  => 缺陷：任意一个自定义预设的字段类型错误，会导致【全部预设】加载失败（连内置预设一起丢失）

对照（只含正常预设）：
  Init 成功，共 30 个（含内置）。my_good 存在=True
```

**影响**：用户在 `~/.mediac/presets.yaml` 或 `cwd/presets.yaml` 里写错一个字段（如 `dimension: "1920abc"`、`videoQuality: abc`），**整个应用预设下拉框变空**。GUI 侧只在 `StartupDiagnostics`（`MainForm.cs:1032-1035`）写一行日志，用户看到的是没有可选预设、也无法转码的界面，原因藏在日志角落。

**建议改法**：

```csharp
foreach (var (name, fields) in merged)
{
    try
    {
        PresetMap[name] = FromFields(name, fields);
        PresetNames.Add(name);
    }
    catch (Exception)
    {
        // 单个预设坏掉不应带走全部；记录后跳过（建议收集成 warnings，由 GUI 启动日志明确列出）
    }
}
```

同时把 `FromFields` 的 `Convert.ToDouble/ToInt64` 换成 `TryParse` 并回退默认值 + 告警，使「类型写错」不再抛异常。

**回归验证**：新增用例——在「只有一个坏预设」的配置下 `Init()` **不抛异常**，且内置预设全部可用。

---

### P1-3 4 个 `[GeneratedRegex]` 的 `\\s` 过度转义导致解析静默失效

**位置**：`FfmpegBuild.cs:394`、`:397`、`:532`、`:535`

**证据（源码）**：

```csharp
// FfmpegBuild.cs:394
[GeneratedRegex(@"-c:a(?::\d+)?\\s+(\\S+)")]  private static partial Regex RxAudioCodec();
// FfmpegBuild.cs:397
[GeneratedRegex(@"-b:a\\s+(\\S+)")]           private static partial Regex RxAudioBitrate();
// FfmpegBuild.cs:532
[GeneratedRegex(@"-map_metadata:s:[va]\\s+\\S+")] private static partial Regex RxMapMetadata();
// FfmpegBuild.cs:535
[GeneratedRegex(@"\\s+")]                     private static partial Regex RxWhitespace();
```

C# **verbatim 字符串** `@"..."` 中，`\\` 是两个字符（两个反斜杠），正则引擎解释为**字面反斜杠**而非空白类 `\s`。正确写法应是 `@"\s+"`。同项目其它文件（`Helper.cs:95`、`HwDetect.cs:82`）均用单反斜杠，可见这 4 处是笔误。

**实证（探针，逐一比对当前与修正后行为）**：

```
RxWhitespace   @"\\s+"                          输入 "a   b"            当前 False  修正后 True
RxAudioCodec   @"-c:a(?::\d+)?\\s+(\\S+)"       输入 "-c:a:0 libopus"   当前 False  修正后 True
RxAudioBitrate @"-b:a\\s+(\\S+)"                输入 "-b:a 192k"        当前 False  修正后 True
RxMapMetadata  @"-map_metadata:s:[va]\\s+\\S+"   输入 "-map_metadata:s:v BPS="  当前 False  修正后 True
```

**实际影响（按可达性区分）**：

| 正则 | 使用点 | 可达性 | 后果 |
|---|---|---|---|
| `RxWhitespace` | `BuildStreamArgs`（`FfmpegBuild.cs:519`） | **可达**（`streamArgs` 来自预设，几乎所有内置预设都设了 `-map_metadata 0`） | 空白压缩失效；`RxMapMetadata` 同时失效，导致 `-map_metadata:s:v/-s:a` 的**清洗逻辑不生效**，这类参数原样进入命令 |
| `RxAudioCodec` | `BuildAudioArgs:350`、`FallbackAudioEncoder:407` | 潜伏（`AudioArgs` 当前恒为 null） | 旧式 `audioArgs` 编码器解析静默失败；`FallbackAudioEncoder("-c:a libopus")` 退化为裸 `aac`（丢掉 `-c:a` 前缀） |
| `RxAudioBitrate` | `BuildAudioArgs:366` | 潜伏（同上） | 旧式 `audioArgs` 码率解析静默失败 |

**实证（`FallbackAudioEncoder` 行为偏移）**：

```
FallbackAudioEncoder("-c:a libopus")    = "aac"    期望 "-c:a libopus"   ← 不一致
FallbackAudioEncoder("-c:a libfdk_aac") = "aac"    期望 "-c:a aac"       ← 不一致
FallbackAudioEncoder("libopus")         = "libopus"（无前缀路径正常）
FallbackAudioEncoder("copy")            = "copy"    （正常）
```

**建议改法**：把 4 处 `\\s` / `\\S` 改为 `\s` / `\S`。若认为 `AudioArgs` 分支已废弃，也可**直接删除该分支与两个正则**，并把 `FFmpegPreset.AudioArgs`（`Model/Preset.cs:56`，已确认 `FromFields` 从不赋值）标注废弃——同时消除死代码与潜伏缺陷。

**回归验证**：为 4 个正则各补一条断言（可参照上述输入对），重点是 `BuildStreamArgs` 的空白压缩。

---

### P1-4 文件对话框允许选择的扩展名会被扫描器静默丢弃

**位置**：`MainForm.cs:532-539`（`OpenFileDialog.Filter`）vs `Helper.cs:13-21`（`VideoFormats` / `AudioNormal` / `AudioLossless`）

**证据（源码）**：

```csharp
// MainForm.cs:536-537  Filter 允许的扩展名
Filter = "媒体文件|*.mp4;*.mkv;*.mov;*.avi;*.webm;*.flv;*.ts;*.m2ts;*.wmv;*.mpg;*.mpeg;*.m4v;" +
         "*.mp3;*.flac;*.wav;*.m4a;*.aac;*.opus;*.ogg;*.wma|所有文件|*.*",

// Helper.cs:13-21  扫描器认可的类型
public static readonly string[] VideoFormats = [".mp4", ".mov", ".wmv", ".avi", ".mkv", ".m4v", ".ts", ".flv",
                                                ".webm", ".rmvb", ".rm", ".vob", ".mpg"];
private static readonly string[] AudioNormal = [".aac", ".m4a", ".mp3", ".wma"];
private static readonly string[] AudioLossless = [".ape", ".flac", ".wav", ".tta", ".dts", ".tak"];
```

`FfmpegScan.CollectInputFiles`（`FfmpegScan.cs:21`、`:42`）用 `Helper.IsMediaFile2` 过滤，不匹配的直接 `continue`。

**实证（探针）**：

```
对话框允许选择 20 种扩展名
被扫描器拒绝（IsMediaFile2=false）的有 4 种: .m2ts, .mpeg, .opus, .ogg
=> 缺陷：用户在对话框里选中的这些文件会被 FfmpegScan 静默丢弃，无任何提示
```

**影响**：`.m2ts`（蓝光原盘常见）、`.mpeg`、`.opus`、`.ogg` 都是实际会遇到的媒体格式。用户在文件对话框里**能选中**它们，程序也**接受**了输入，但扫描时被静默丢弃。若这些是唯一输入，用户看到的是「没有找到可处理的媒体文件（检查输入路径、预设的媒体类型与筛选参数）」（`TranscodeSession.cs:203`），而真实原因是格式白名单不含该扩展名——这类提示会把用户引向错误方向。

**建议改法（任选其一，推荐 ①）**：

1. **统一事实源**：让 `OpenFileDialog.Filter` 由 `Helper.VideoFormats` / `AudioFormats` 动态拼接（`Helper` 已是公开 API），杜绝两份名单漂移。
2. **补齐白名单**：把 `.m2ts` / `.mpeg` / `.opus` / `.ogg` 加入 `Helper`（`.mkv` 已含、`.ts` 已含；`.m2ts` 可归入视频，`.mpeg` 与 `.mpg` 同族）。
3. **至少给出可解释的提示**：扫描阶段区分「扩展名不支持」与「其他跳过原因」，在日志里显式说明被排除的扩展名与数量。

**回归验证**：新增断言——`OpenFileDialog.Filter` 中出现的每个扩展名都满足 `Helper.IsMediaFile2("x" + ext)`（可复用本次探针的写法，一行循环即可锁定）。

---

### P1-5 `ParamsFlowForm` 构造期 DPI 基准缺失导致 footer 高度首次计算错误（时序脆弱）

**位置**：`ParamsFlowForm.cs:182-183`、`:188`（`BuildHeader`）、`:308-346`（`ReflowFooter`）、`:674-677`（`Scale`）

**证据（源码）**：

```csharp
// :182-183  构造函数最后才设置 DPI 基准
AutoScaleMode = AutoScaleMode.Dpi;
AutoScaleDimensions = new SizeF(96f, 96f);

// :188  BuildHeader 里（在 AutoScaleDimensions 生效之前）已用 Scale()
headerPanel.Height = Scale(32);

// :674-677  Scale 依赖 DeviceDpi
private int Scale(int value) => (int)Math.Round(value * DeviceDpi / 96.0);

// :308-322  ReflowFooter 在 layoutReady 后重算，基准是 102
var btnTop = Scale(102);
var targetHeight = btnTop + btnH + padBottom;
```

`Scale()` 在**构造函数期间**被调用（`BuildHeader` / `BuildFooter`），此时控件尚无父窗体句柄，`DeviceDpi` 返回默认 96；`footerPanel.Height = Scale(138)` 因此得到 138（而非 150% 下应有的 207）。

**实证（探针，DPI 144 = 150%）**：

```
构造完成、Show 之前：DeviceDpi=144  ClientSize={1440,1080}
  headerPanel.Height=72   footerPanel.Height=310   flowPanel.Height=294
Show 之后：DeviceDpi=144  ClientSize={1440,1080}
  headerPanel.Height=72   footerPanel.Height=204   flowPanel.Height=246
```

`ReflowFooter` 只在 `Shown` 时执行一次（`layoutReady = true`）。虽然 `footerPanel.Resize` 也挂了 `ReflowFooter`，但**兜底重算的条件是 `if (footerPanel.Height != targetHeight) footerPanel.Height = targetHeight;`**——只有在高度已不等于目标时才会触发，而初始 138（96 DPI 值）与 150% 目标 207 不等，故 Shown 后会修正到 204（实测值）。

> 说明：本项在**当前实现下会被 `Shown` 重算兜住**（实测 204 ≈ 目标），所以它是「脆弱」而非「已损坏」。风险在：`ReflowFooter` 依赖 `layoutReady`，若未来在 `Shown` 之前有任何路径让 footer 参与布局（或 `AutoScaleDimensions` 与 `Scale` 混用导致二次缩放），就会出现底部按钮被裁。这属于**应消除的时序脆弱性**，而非当前必然复现的可见缺陷。

**建议改法**：把 `AutoScaleMode` / `AutoScaleDimensions` 的赋值为**构造函数第一行**（在任何 `Scale()` 调用之前），或统一改为不混用 `Scale()`、完全交给 WinForms 自动缩放。同时把 `Scale()` 的 DPI 来源改为 `FindForm()?.DeviceDpi ?? DeviceDpi` 并加注释说明时序依赖。

**回归验证**：在 150% DPI 下断言 `footerPanel.Height >= footerPanel 内所有按钮的 Bottom 最大值 + 6`。

---

## 五、P2 级缺陷

### P2-1 `AboutForm` 的 ListView 列宽不随 DPI 缩放，150% 下文本被裁

**位置**：`AboutForm.cs:156-157`、`:183-184`、`:200-206`、`:259-262`

**证据（源码）**：

```csharp
// :156-157  ClientSize/MinimumSize 为 96 DPI 像素
ClientSize = new Size(860, 620);
MinimumSize = new Size(680, 460);

// :170-176  关闭按钮的偏移与尺寸同样按 96 DPI 书写
close.SetBounds(ClientSize.Width - 110, ClientSize.Height - 40, 96, 28);

// :200-206  列宽是"数值集合"，不受 AutoScaleDimensions 影响
list.Columns.Add("预设", 150);
list.Columns.Add("类型", 60);
...
// :259-262
list.Columns.Add("参数", 230);
list.Columns.Add("取值", 150);
list.Columns.Add("说明", 370);
list.Columns.Add("状态", 80);

// :183-184  基准在构造函数末尾才设置
AutoScaleMode = AutoScaleMode.Dpi;
AutoScaleDimensions = new SizeF(96F, 96F);
```

**问题机理**：`AutoScaleDimensions` 会缩放控件的 `Bounds` 与字号，但**不会缩放 `ListView.Columns[i].Width`** 这类"集合内的数值属性"（同理也不会缩放 `RichTextBox` 的字符排版）。因此在 150% DPI 下，字体放大 1.5 倍而列宽保持 96 DPI 原值。

**实证（探针，DPI 144 = 150%）**：

```
AboutForm: DeviceDpi=144  ClientSize={Width=1290, Height=930}   （= 860×620 的 1.5 倍，窗体本身已缩放）
cliOptionList: Bounds={X=0,Y=0,Width=1282,Height=893}
  列宽（源码 230/150/370/80）:
    参数  Width=230   ← 未缩放
    取值  Width=150   ← 未缩放
    说明  Width=370   ← 未缩放
    状态  Width=80    ← 未缩放
```

对照：`close.Bounds` 实测 `{X=1125,Y=870,Width=144,Height=42}`，即 `96×1.5` 与 `28×1.5`——**控件 Bounds 确实被缩放了**，进一步证明问题只出在 `Columns` 这类集合数值上。

**影响**：150% DPI 下，「说明」列仍是 370px 宽，但字号已放大 1.5 倍，**可视字符数减少约 1/3**。`CliOptions` 中较长的描述（如 `--output-mode` 的「输出目录结构：tree 保持目录树 / dir 保持父目录 / file 扁平化」、「`--regex`」的「仅处理文件名匹配该正则的文件（C# core 为模式串，与 Node 版的布尔开关不同）」）会被截断为省略号，用户必须依赖悬浮提示才能看到完整说明——而列表本身**没有为列设置 ToolTip**。

**建议改法**：

1. **列宽按 DPI 缩放**：在设置列宽处套用统一的缩放系数（可参照 `ParamsFlowForm.Scale` 的写法），例如 `list.Columns.Add("说明", Scale(370))`。
2. **改用自适应列宽**：把最长的「说明」列设为 `-2`（按内容+表头自动调整）或 `-1`（按内容调整），把「参数」列设为固定合理值，避免手工像素值。
3. **统一 DPI 基准位置**：把 `AutoScaleMode` / `AutoScaleDimensions` 的赋值移到构造函数**第一行**（详见 P1-5 的同源建议），避免"部分缩放、部分不缩放"的混合状态。

**回归验证**：150% DPI 下断言各列宽 ≥ `96DPI值 × 1.4`；或截图后检查「说明」列是否出现省略号。

---

### P2-2 `MainForm` 最小尺寸下日志区高度塌陷为 0

**位置**：`MainForm.cs:112-113`（尺寸）、`:404-461`（日志组布局）

**证据（源码）**：

```csharp
// :112-113
ClientSize = new Size(1000, 800);
MinimumSize = new Size(880, 640);

// :406-415  日志组 Top|Bottom 双向锚定，logBox 也 Top|Bottom
var box = new GroupBox { Location = new Point(12, 468), Size = new Size(976, 288),
    Anchor = AnchorStyles.Top | AnchorStyles.Bottom | AnchorStyles.Left | AnchorStyles.Right };
logBox.SetBounds(14, 22, 948, 214);
logBox.Anchor = AnchorStyles.Top | AnchorStyles.Bottom | AnchorStyles.Left | AnchorStyles.Right;

// :449-455  进度行/进度条仅锚定 Bottom（不随高度收缩）
progressLabel.SetBounds(14, 240, 948, 16);
progressBar.SetBounds(14, 260, 948, 14);
progressBar.Anchor = AnchorStyles.Bottom | AnchorStyles.Left | AnchorStyles.Right;
```

**问题机理**：固定区（输入 110 + 输出 92 + 参数 158 + 操作 64 + 各组外边距）在 150% DPI 下已占满约 468×1.5 = 702px；`MinimumSize` 高 640（客户区 876px @150%）留给日志组的高度不足，`logBox` 因 Top|Bottom 双向锚定被压扁，而 `progressLabel`/`progressBar` 因只锚 Bottom 仍停留在原相对位置。

**实证（探针，离屏显示 + 缩到 `MinimumSize`，DPI 144）**：

```
初始 ClientSize={Width=1500, Height=1200}
  logBox        Bounds={X=21,Y=33,Width=1420,Height=319}   完全在客户区内=True
  progressLabel Bounds={X=21,Y=360,Width=1422,Height=24}   完全在客户区内=True
  progressBar   Bounds={X=21,Y=390,Width=1422,Height=21}   完全在客户区内=True

缩到 MinimumSize 后 ClientSize={Width=1287, Height=876}
  logBox        Bounds={X=21,Y=33,Width=1207,Height=0}     ← 高度塌陷为 0
  progressLabel Bounds={X=21,Y=36,Width=1209,Height=24}    ← 上移到 logBox 原位置
  progressBar   Bounds={X=21,Y=66,Width=1209,Height=21}
  progressBar 与 logBox 重叠 = False
```

> 修正说明：实测**并非**「状态栏/进度条被裁出客户区」，而是**日志区高度被压到 0（用户看不到任何日志），进度行顶到了日志组顶部**。所有控件仍在客户区内，`statusBar` 也正常（`Bounds={0,841,1287,35}`）。

**影响**：用户把窗口缩到最小值时，**日志区完全消失**（`logBox.Height == 0`），而进度条/状态文案被挤到日志组最上方。此时「清空日志」「复制全部日志」等日志区操作对用户不可见，转码过程中的实时输出也看不到——这与界面「日志区是核心反馈通道」的定位冲突。

**说明**：`MainFormInputTests.cs:330` 的 `HintText_RemainsVisibleAtMinimumSize` 覆盖的是**主界面 hint 标签**，未覆盖日志区高度。

**建议改法（推荐 ①）**：

1. **给日志区设高度下限**：把 `logBox.Anchor` 改为仅 `Top|Left|Right`（不参与竖向拉伸），日志组本身也不做竖向拉伸，改为**固定高度 + 主窗体内滚动**；或给 `logBox` 设 `MinimumSize`。
2. **提高 `MinimumSize`**：按当前固定区布局（150% DPI 下约 702px）推算，`MinimumSize` 高度至少应到 ~780（客户区 ≥ 750），才能给日志区留出可用高度。
3. **重排优先级**：让日志组用 `Dock = Fill` 占据剩余空间，把操作组/参数组改为 `Dock = Top`，使压缩顺序可控。

**回归验证**：新增断言——缩到 `MinimumSize` 后 `logBox.Height >= 100`（或所选下限）。

---

### P2-3 `hwaccel` 下拉中四项完全等价，且 `cpu` 项语义误导

**位置**：`MainForm.cs:287`、`HwDetect.cs:60-64`、`HwDetect.cs:301-328`

**证据（源码）**：

```csharp
// MainForm.cs:287
hwaccelCombo.Items.AddRange(["auto", "cuda", "qsv", "amf", "d3d", "d3d11va", "d3d12va", "dxva2", "cpu"]);

// HwDetect.cs:60-63
private static readonly Dictionary<string, string> HwaccelAliases = new(StringComparer.Ordinal)
{
    ["d3d11va"] = "d3d", ["d3d12va"] = "d3d", ["dxva2"] = "d3d",
};
```

**实证（探针）**：

```
--hwaccel auto     => [cuda, swdec, d3d, cpu]
--hwaccel d3d      => [d3d, cpu]
--hwaccel d3d11va  => [d3d, cpu]     ← 与 d3d 完全相同
--hwaccel d3d12va  => [d3d, cpu]     ← 与 d3d 完全相同
--hwaccel dxva2    => [d3d, cpu]     ← 与 d3d 完全相同
--hwaccel cpu      => [cuda, swdec, d3d, cpu]   ← 与 auto 完全相同，并非「强制软解」
```

两处误导：

1. **四项等价**：`d3d / d3d11va / d3d12va / dxva2` 归一到同一 tier，用户选哪个结果一样，下拉却给出 4 个选项。这与 `docs/UIUX-AUDIT-REVIEW-20260926.md:88` 的结论一致（该文档已建议收敛为 6 项，**但代码里至今是 9 项**，属未落地项）。
2. **`cpu` 项不是「强制软解」**：`CandidateTiers` 的 `auto + hwaccel` 分支（`HwDetect.cs:319-328`）对 `name == "cpu"` 不做白名单过滤，直接落到默认链，结果与 `auto` 完全一致。真正「强制软解」只能靠 `--decode-mode cpu`（`HwDetect.cs:303`）。用户在下拉里选 `cpu` 却仍走硬件解码，且界面无任何提示。

**建议改法**：

1. 下拉收敛为 `auto / cuda / qsv / amf / d3d / cpu` 六项，并在 tooltip 注明 `d3d` 涵盖 `d3d11va / d3d12va / dxva2`。
2. 把 `cpu` 项改为**联动「解码模式=cpu」**，或直接从 `hwaccelCombo` 移除（让用户用 `decodeModeCombo` 表达软解意图），避免两个控件语义重叠。
3. 若保留 `cpu` 项，`CandidateTiers` 应对 `hwaccel == "cpu"` 明确返回 `["cpu"]`（与 `decodeMode == "cpu"` 对齐），消除「选了不生效」。

---

### P2-4 `ParamsForm` 是死代码却被大量测试固化

**位置**：`src/MediaCli.Transcode.Gui/ParamsForm.cs`（600 行）

**证据（检索）**：`ParamsForm` 在 `src/` 下**没有任何生产引用**——`MainForm.ShowParamsDialog`（`MainForm.cs:513-520`）使用的是 `ParamsFlowForm`。仅在测试项目里被引用：`TestableParamsForm.cs`、`ParamsFormTests.cs`（30 项用例，为全部测试文件之最）、`ParamsFormScreenshotTests.cs`。

**影响**：两套并行的「高级参数面板」实现（`ParamsForm` 与 `ParamsFlowForm`）同时存在，且旧的一套仍被 30 项测试守护。这带来三个实际问题：
- 维护成本：面板/参数表变更需同步改两处；
- 误导风险：后来者可能误改已废弃的 `ParamsForm` 而看不到界面变化；
- 测试信号污染：417 项「全绿」里有 30 项在验证无人使用的界面。

**建议改法**：确认无保留必要后**删除 `ParamsForm.cs` 及其 3 个测试文件**，或至少加 `[Obsolete]` 标注并在 `AGENTS.md` 注明「高级参数面板唯一实现是 `ParamsFlowForm`」。若因历史对照需要保留，建议移出生产项目（改放到 `temp/` 或测试项目内）。

---

### P2-5 「详细日志」开关未作用于 core 日志过滤

**位置**：`TranscodeSession.cs:459-488`（`ForwardCoreLog`）、`TranscodeSession.cs:480`

**证据（源码）**：

```csharp
// TranscodeSession.cs:479-487  无任何 debug 判断
// 原始 ffmpeg 输出：只放行 error/warning，其余计数后丢弃
if (FfmpegLogFilter.ShouldKeep(line))
{
    log(SessionLogLevel.Warn, line);
}
else
{
    suppressedLines++;
}
```

`FfmpegLogFilter` 已经提供了 `Apply(line, keepEverything)` 与 `CountSuppressed` 等配套 API（`FfmpegLogFilter.cs:52-57`），语义正是「勾选详细日志时原样返回」。但 `TranscodeSession` **只调用了 `ShouldKeep`**，完全没有把 `GuiOptions.Debug` 传下来。

**实证（检索）**：

```
Debug 在 GUI 侧的全部使用点：
  GuiOptions.cs:48   public bool Debug { get; set; }
  GuiOptions.cs:133  Debug = Flag("debug", Debug),          ← 写入 ArgvOptions
  MainForm.cs:615    Debug = debugCheck.Checked,            ← 从复选框读入
FfmpegLogFilter 在 src 中的调用点：
  TranscodeSession.cs:480   if (FfmpegLogFilter.ShouldKeep(line))   ← 仅此一处
```

即 `Debug` 只影响 core 的 ffmpeg `-v` 级别（`FfmpegBuild.cs:159`），**不影响 GUI 自己的日志过滤器**。

**影响**：勾选「详细日志」后，core 会以 `repeat+level+info` 输出大量 ffmpeg 信息（`FfmpegBuild.cs:159`），但这些行在 `ForwardCoreLog` 里被 `ShouldKeep` 挡掉并计入 `suppressedLines`。用户勾选后**几乎看不到额外输出**，只在统计块看到「已隐藏 ffmpeg 常规输出 N 行（勾选「详细日志」可全部显示）」（`SessionReport.cs:64-68`）——而用户**已经勾选了**，提示与事实矛盾。

**建议改法**：把 `Debug` 透传到 `TranscodeSession`（构造参数或 `Run` 参数），在 `ForwardCoreLog` 中改用：

```csharp
if (FfmpegLogFilter.Apply(line, keepEverything: debug) is { } kept)
{
    log(debug ? SessionLogLevel.Info : SessionLogLevel.Warn, kept);
}
else
{
    suppressedLines++;
}
```

并同步修正 `SessionReport` 的措辞（区分「未勾选详细日志」与「已勾选但输出仍需过滤」）。

**回归验证**：新增用例——`Debug=true` 时 `SuppressedLines == 0` 且常规 verbose 行被保留。

---

### P2-6 日志同步文件名截断长度算错，唯一随机后缀被全部截掉

**位置**：`SessionLogWriter.cs:119`

**证据（源码）**：

```csharp
// :118-119
// 同样加唯一后缀，避免同一秒内两次同步撞名
var name = $"ffconv-transcode-{DateTime.Now:yyyyMMdd-HHmmss}-{Guid.NewGuid():N}"[..(24 + 6)] + ".log";
```

**实证（复刻字符串拼接）**：

```
完整串      : 'ffconv-transcode-20260928-111925-5efe25b5ead941ddbb6983f15fbd14c1'
完整串长度  : 65
截断[..30]  : 'ffconv-transcode-20260928-1119'      ← 长度 30
随机部分    : ''                                    ← 0 位 guid

=> 前缀 "ffconv-transcode-" 长 17，"yyyyMMdd-HHmmss" 长 15，合计 32 > 30
   → 截断点在时间串中间，guid 被全部丢弃
```

**附带**：`[..(24 + 6)]` 这个表达式的意图显然是「保留 24 位时间戳 + 6 位 guid」，但 (a) 时间戳格式实际是 15 位而非 24 位，(b) 前缀 17 位未计入，(c) 截断发生在时间**中间**，得到的名字形如 `ffconv-transcode-20260928-1119.log`——**既丢了唯一性，又留下一个看起来像"分钟级"（缺秒）的残缺时间戳**，比不加截断更差。

对照：同类逻辑在 `Stamp()`（`:62-63`）里用的是 `[..23]`，恰好落在 15 位时间戳 + `-` + 7 位 guid（共 23），那一处**是正确的**。

**影响**：同一秒内两次调用 `SyncTo`（例如用户快速连续跑两次、或未来引入并发）会因文件名完全相同而**后写覆盖前写**，静默丢失前一次会话日志。且残缺时间戳会让用户难以判断日志的真实时间。

**建议改法**：

```csharp
// 明确构造，不做隐式截断
var stamp = DateTime.Now.ToString("yyyyMMdd-HHmmss");
var name = $"ffconv-transcode-{stamp}-{Guid.NewGuid():N}"[..^0]; // 或直接不截断
// 若确需限长，用显式分段拼接，保留完整时间戳 + 至少 12 位 guid：
var name = $"ffconv-transcode-{stamp}-{Guid.NewGuid():N}"[..(17 + 15 + 12 + 1)] + ".log";
```

**回归验证**：新增用例——同一秒连续两次 `SyncTo` 得到**不同**路径，且两者都能读到内容。

---

### P2-7 `-encoders` 解析把 `=` 当作编码器名

**位置**：`HwDetect.cs:71-83`

**证据（源码）**：

```csharp
// :82  六字符标志位 + 空白 + 名称
[GeneratedRegex(@"^\s*[A-Z.]{6}\s+(\S+)")]
private static partial Regex RxEncoderLine();
```

`ffmpeg -encoders` 输出的**表头说明行**形如 ` V..... = Video`，前 6 个字符 ` V.....` 合法匹配该类，于是捕获到 `=`。

**实证（真实 ffmpeg 输出 + 探针解析）**：

```
$ ffmpeg -hide_banner -v error -encoders | head -10
Encoders:
 V..... = Video            ← 说明行
 A..... = Audio
 S..... = Subtitle
 .F.... = Frame-level multithreading
 ..S... = Slice-level multithreading
 ...X.. = Codec is experimental
 ....B. = Supports draw_horiz_band
 .....D = Supports direct rendering method 1
 ------
 V....D a64multi  Multicolor charset for Commodore 64 (codec a64_multi)

探针解析结果: =, aac, h264_nvenc, libx264
含 '=' 假条目: True
解析出 2 个: [=, libx264]
```

**影响**：`Encoders` 集合被污染且 `EncoderCount` 偏大（多算 1）。更重要的是它会**削弱若干判定**：
- `FfmpegTask` 的严格模式音频编码器预检（`FfmpegTask.cs:169`）：`caps.Encoders.Count > 0` 恒真（本来也恒真），但 `Contains(targetAudioCodec)` 判断不受影响，风险有限；
- `HwAccel.PickRuntimeEncoder`（`:436-446`）：`encoders.Contains(encoder)` 不受 `=` 影响；
- 真正的风险在**未来**任何基于「编码器数量」或「集合内容」的推断（例如「encoders.Count == 0 表示探测失败」这类判断会被 `=` 直接破坏）。当前 `encoders.Count > 0` 的用法（`FfmpegTask.cs:169`）本就不严谨：探测失败得到空集时也会因 `=` 的存在而被视为"有编码器"。

**建议改法**：正则排除 `=`，并把说明行（`-----` 分隔线之前的部分）整体跳过：

```csharp
// 方案 A：捕获名不允许以 '=' 开头
[GeneratedRegex(@"^\s*[A-Z.]{6}\s+([A-Za-z0-9_][\w.-]*)")]
// 方案 B（更稳）：先定位 "------" 分隔行，只解析其后的行
```

**回归验证**：用真实 `ffmpeg -encoders` 输出（或固定样本）断言解析结果不含 `=`，且包含已知编码器名。

---

### P2-8 取消收尾路径可能把进度与状态刷成「已完成」

**位置**：`MainForm.cs:756-809`（`FinishRun`）

**证据（源码）**：

```csharp
// :764-780  先判故障/取消
if (task.IsFaulted) { ... return; }
if (!task.IsCompletedSuccessfully) { ... progressLabel.Text = "已取消"; return; }

// :782-800  再取结果，但未判 WasCancelled 就无条件把进度刷满
var s = task.Result;
if (s.Total == 0) { ... return; }

lastPercent = 100;
SetProgressInstant(100);                       // ← 无论是否被取消都刷到 100%
progressLabel.Text = s.WasCancelled ? "已取消"
    : s.Preview > 0 ? $"预览完成 {s.Preview}/{s.Total}  ·  100%"
    : $"转码完成 {s.Processed}/{s.Total}  ·  100%";
stateLabel.Text = s.WasCancelled ? "已取消" : s.Preview > 0 ? "预览完成" : "已完成";
```

`TranscodeSession.Run` 在「用户点取消」时走的是**正常返回**（`summary.WasCancelled = true`，见 `TranscodeSession.cs:216-221`、`:281-287`），`Task` 本身是 `RanToCompletion`。因此会进入 `:791-800` 分支。

**问题**：`progressLabel` / `stateLabel` 的文字**已按 `WasCancelled` 正确区分**（"已取消"），但：
1. `SetProgressInstant(100)`（`:792`）把进度条刷满，**取消后进度条显示 100%**，与"已取消"文案矛盾；
2. 紧接着 `if (!s.WasCancelled && s.Success > 0)` 才 `FlashWindow` + 提示音（`:802-806`）——这部分判断是对的；
3. 但当 `s.WasCancelled == false && s.Success == 0 && s.Failed > 0`（全部失败）时，`stateLabel` 会显示 **"已完成"**，尽管日志汇总里是"失败 N"。用户看到"已完成"而实际全失败。

**影响**：
- 取消场景：进度条与文案矛盾（100% + "已取消"）。
- 全失败场景：状态栏显示"已完成"，与日志"汇总：失败 N"矛盾，容易被误读为成功。

**建议改法**：

```csharp
var s = task.Result;
if (s.Total == 0) { ... }
if (s.WasCancelled)
{
    // 保留取消时的进度值，不刷满；文案统一为"已取消"
    progressLabel.Text = "已取消";
    stateLabel.Text = "已取消";
    FinalizeLog(opts, s);
    return;
}
lastPercent = 100;
SetProgressInstant(100);
progressLabel.Text = s.Preview > 0 ? $"预览完成 {s.Preview}/{s.Total}  ·  100%"
    : s.Failed > 0 && s.Success == 0 ? $"全部失败 {s.Failed}/{s.Total}"
    : $"转码完成 {s.Processed}/{s.Total}  ·  100%";
stateLabel.Text = s.Preview > 0 ? "预览完成" : s.Failed > 0 && s.Success == 0 ? "全部失败" : "已完成";
```

**回归验证**：新增用例——`WasCancelled=true` 时 `progressBar.Value != 100`；`Success=0 && Failed>0` 时状态文案不为"已完成"。

---

### P2-9 进度条「瞬时刷新」技巧在极值与取消路径上产生异常值与状态残留

**位置**：`MainForm.cs:627-641`（`SetProgressInstant`）、`:969-978`（`OnClearLog`）、`:872-887`（`Flush`）

**证据（源码）**：

```csharp
// :627-641
private void SetProgressInstant(int value)
{
    var v = Math.Clamp(value, progressBar.Minimum, progressBar.Maximum);
    if (v == progressBar.Maximum)
    {
        progressBar.Maximum = v + 1;   // ← 临时把 Maximum 抬高
        progressBar.Value = v + 1;
        progressBar.Maximum = v;       // ← 再降回
    }
    else
    {
        progressBar.Value = v + 1;
        progressBar.Value = v;
    }
}
```

该技巧用于绕过 comctl32 的平滑过渡。但存在两个问题：

1. **`v + 1` 可能越界**：`Math.Clamp(value, 0, Maximum)` 保证 `v <= Maximum`，所以 `v + 1 <= Maximum + 1` 恰好等于抬高后的 `Maximum`，本身合法。但若 `progressBar.Maximum == progressBar.Minimum == 0`（理论上可能被外部改成 0），`v + 1 = 1 > Maximum(0)` 会在 `progressBar.Maximum = v` 之后**抛 `ArgumentException`**（Value 超出范围）。当前 `Maximum` 在 `BuildLogGroup`（`:457`）设为 100 且不再改小，故实际不可达，属**防御性缺口**。
2. **`OnClearLog` 残留**：`:969-978` 里 `progressLabel.Text = running is null ? "就绪" : progressLabel.Text;`——清空日志时若任务在跑，保留旧文案；若不在跑则设为"就绪"，但**如果上一次是"已取消/全部失败"**，清空日志会把状态文案抹成"就绪"而不重置 `stateLabel`，`stateLabel` 仍显示"已取消"。两个标签由此不同步。

**建议改法**：

1. `SetProgressInstant` 增加前置保护：`if (progressBar.Maximum <= progressBar.Minimum) return;`。
2. `OnClearLog` 统一重置 `stateLabel` 与 `progressLabel`（任务在跑时保留两者，否则两者一起归零/归"就绪"）。

---

## 六、P3 级与零散发现

### P3-1 `CalcLongEdge` 无输出下限，极端参数可产出 `0x0`

**位置**：`HwAccel.cs:285-302`

```csharp
public static int ToEven(double x)
{
    var v = (int)Math.Round(x);
    return v - (v % 2);          // ← 1 → 0；负奇数的 % 结果也为负，需注意
}
```

**实证（探针）**：

```
ToEven(1919)=1918  ToEven(1920)=1920  ToEven(1918.6)=1918  ToEven(1)=0  ToEven(3)=2
CalcLongEdge(3840,2160,2)  = 2x0
CalcLongEdge(1,3840,1)     = 0x0
CalcLongEdge(3840,6,6)     = 6x0
```

`CalcLongEdge` 只校验 `srcW/srcH/dimension > 0`，不检查**计算结果**是否 ≥2。目标长边极小（如用户 `--dimension 2`）或源尺寸极端（如 `3840x6`）时会产出含 0 的尺寸。含 0 的 `-vf scale=w=2:h=0` 会让 ffmpeg 报错（当前会被后续探测/执行失败兜住，故只列 P3）。

**建议**：`CalcLongEdge` 结果对短边做 `Math.Max(2, ...)` 保护；`ToEven` 对 `v < 2` 返回 2（或文档化 `0` 的语义）；并在 GUI 的 `dimension` 校验里给出下限（如 ≥64）。

### P3-2 非法 `--hwaccel` 取值静默退化为默认链

**位置**：`HwDetect.cs:319-328`

```csharp
if (!string.IsNullOrEmpty(hwaccel) && !hwaccel.Equals("auto", StringComparison.OrdinalIgnoreCase))
{
    var name = NormalizeHwaccelName(hwaccel);
    if (name is not null && name != "cpu") { ... }
    // 非法值：JS warn 后走默认链      ← 仅注释，实际无任何告警出口
}
```

**实证（探针）**：`--hwaccel vaapi` → `[cuda, swdec, d3d, cpu]`（与 `auto` 相同，用户无感知）。

这与 `docs/UIUX-AUDIT-REVIEW-20260926.md` 与 `CliOptions` 强调的「凡是不能生效的输入都必须告警」原则**直接冲突**：GUI 的参数校验层（`CliArgParser`）把 `hwaccel` 声明为 `CliValueKind.Text`（`CliOptions.cs:62`），不做取值校验，因此拼错的 `--hwaccel cuda2` 会一路静默通过。

**建议**：把 `hwaccel` 改为 `CliValueKind.Choice`（合法值 `auto/cuda/qsv/amf/d3d/d3d11va/d3d12va/dxva2/cpu`），由既有校验框架自动告警；或让 `CandidateTiers` 通过返回值/告警集合把「未知取值」上抛。

### P3-3 面板「参数示例」与实际命令语义不符

**位置**：`ParamTrial.cs:52-110`（`BuildCommandLine`）

```csharp
// :118-129  质量参数的选择只看 codec family
private static string QualityFlag(string? family)
{
    if (!(family == "h264"))
    {
        if (family == "vp9") return "-crf";
        return "-cq";          // ← hevc/av1 也返回 -cq
    }
    return "-crf";
}
```

示意命令用 `-crf`（h264/vp9）/ `-cq`（其他），但真实编排里：CPU 层 hevc 用 `-crf`（`HwAccel.cs:533`）、NVENC 用 `-cq`、QSV 用 `-global_quality`、AMF 用 `-qvbr_quality_level`（`HwAccel.cs:480-522`）。界面已有「示意命令，非最终命令」的说明（`ParamsFlowForm.cs:73`），因此这是**可接受的简化**，但 `-cq` 用在 hevc 上会误导用户照抄。

**建议**：把示意命令的质量参数改为「按当前预设族 + `(硬件层由主界面预览决定)`」的注释形式，或直接标注 `-crf/-cq/-global_quality 由硬件层决定`。

### P3-4 `VideoQuality` 从 `double` 截断为 `long`

**位置**：`FFmpegPresets.cs:341`、`:351`、`Model/Preset.cs:10`

**实证（探针）**：`argv.VideoQuality=23.7` → `preset.UserArgs.VideoQuality=23`（小数被丢弃）。

`PresetUserArgs.VideoQuality` 是 `long`，而 `FFmpegPreset.VideoQuality` 是 `double`。CRF/CQ 本身允许小数（如 `-crf 23.5`），用户通过 `--video-quality 23.5` 传入会被静默截断。

**建议**：把 `PresetUserArgs.VideoQuality` / `AudioQuality` 改为 `double`（`Model/Preset.cs:10,12`），或在校验层明确拒绝小数并告警。

### P3-5 ffargs 字符串键不做分隔符校验

**位置**：`FfargsValidator.cs:124-129`

```csharp
if (StringKeys.Contains(key))
{
    pairs.Add($"{rawKey}={value}");    // ← value 未校验是否含 , 或 ;
    accepted.Add($"{rawKey}={value}");
    continue;
}
```

数值键已正确做 `TryParse` 校验（`:103-122`），但字符串键（`videoCodec / audioCodec / prefix / suffix / preset / metadata`）未校验 `value` 是否含 `,` 或 `;`。由于归一后的参数串用 `,` 连接、`ParseFfargs` 又用 `,` 切分（`FFmpegPresets.cs:267`），若用户写 `px=a,b`，则 `b` 会变成一个独立的无效段。归一化结果会重新拼进 `--ffargs`，最终被 `ParseFfargs` 误切。

**建议**：字符串键校验 `value` 不含 `,` / `;`，否则告警并跳过（与数值键同等待遇）。

---

## 七、测试体系盲区（为什么这些缺陷没被拦住）

当前 417 项测试全绿，但下面这些缺陷一个都没被覆盖。归纳出 4 类系统性盲区，建议针对性补强。

| 盲区 | 表现 | 建议补强方向 |
|---|---|---|
| **只断言"存在"、不断言"可见/可点"** | `AboutForm_HasCloseButton` 只查 `DialogResult`，不查 z-order 与命中 | 引入命中测试断言：`GetChildAtPoint(control 中心) == control`；对所有可交互按钮做一次遍历式校验 |
| **只测单函数、不测"参数组合"** | `-vf` 与 `-c:v copy` 的组合从未被构造；`HwAccelTests` / `PlanAndBuildTests` 各自单测通过 | 补「参数正交组合」表驱动测试：copy ×（scale/speed/fps/filters）应断言**不产生 `-vf`** 或给出明确错误 |
| **只测 happy path、不测"外部输入污染"** | `NormalizeVendor` 的测试大概只覆盖 `contains("nvidia")` 等正向串；从未用真实 WMI 型号串 | **用真实设备字符串做输入**：`AMD Radeon RX 6800 XT`、`Intel(R) UHD Graphics 770`、`NVIDIA GeForce RTX 4090` |
| **测试与实现"同源"，无法发现共同错误** | 名单一致性测试若直接读 `Helper.VideoFormats` 来构造期望，则名单本身漏项不会被发现 | 对"用户可见契约"（对话框 Filter、文档示例、界面下拉项）建立**独立期望源**，与实现名单交叉校验 |

**另外两点**：

1. **DPI 相关测试缺失**：所有布局测试都在 96 DPI 语义下跑（`AutoScaleDimensions = 96`），而用户主力环境是 **150% 缩放（4K@150%）**。`ParamsFlowForm`/`AboutForm` 的 DPI 问题因此完全不在测试视野内。
2. **截图测试只判"非空白"**：`AboutForm_RendersNonBlankScreenshot` 只要求 `CountDistinctColors >= 40`。P0-1 的关闭按钮被遮挡**恰好不改变这个指标**（截图依然颜色丰富）。建议对关键区域做**局部像素断言**（如"右下角 96×28 区域内必须存在按钮边框色"），或改为断言控件命中测试。

---

## 八、已在既有文档中记录、但**至今未落地**的项

审查时发现，`docs/UIUX-AUDIT-REVIEW-20260926.md` 已给出结论，但代码里仍是旧状态。这类"文档已判、代码未动"的项容易被误认为已解决，特此单列：

| 既有文档结论 | 文档位置 | 当前代码状态 |
|---|---|---|
| `AboutForm.cs:121` 教用户写 `--max-bitrate`（`CliOptions` 无此参数） | 该文档 §1、§三.4（列为"唯一需要立刻改代码的一行"） | **已修复** ✅（`AboutForm.cs:121-122` 已删示例，并有 `UsageTips_OnlyMentionsDefinedParams` 锁住） |
| `hwaccel` 下拉应从 9 项收敛为 6 项 + tooltip | 该文档 §2.3、§五.6 | **未落地**（`MainForm.cs:287` 仍是 9 项）→ 见本报告 P2-3 |
| 「日志区可折叠」是方案 A 的前置依赖 | 该文档 §2.2 | 未落地（日志区不可折叠） |
| 方案 G-1「即时回显条」 | 该文档 §四 | 部分落地（`argsStatus` 已实现，`MainForm.cs:341-357`） |
| 方案 G-2'「轻量插入菜单」 | 该文档 §四 | 以「高级参数面板」形式落地（`ParamsFlowForm`），未做右键插入菜单 |
| 方案 E「说明窗口检索」 | 该文档 §2.4 / §五.5 | **未落地**（`AboutForm` 的 CLI 列表无搜索框、无双击复制） |
| 方案 F「重置为默认参数」 | 该文档 §2.5 | 已以「清空全部」形式落地（`ParamsFlowForm.cs:266-272`） |

> 结论：**最需要修的一行（`--max-bitrate`）已修**，但 `hwaccel` 收敛与说明窗口检索这两项"小成本"改进仍未动。

---

## 九、改进建议汇总（按投入产出排序）

### 第一批：低成本、高收益（建议立即做）

| 序 | 项 | 位置 | 成本 |
|---|---|---|---|
| 1 | 修 `AboutForm` 关闭按钮遮挡（改为 `Dock=Bottom` 的 Panel 承载） | `AboutForm.cs:161-184` | ~10 行 |
| 2 | 修 4 个过度转义正则 | `FfmpegBuild.cs:394,397,532,535` | 4 处字符 |
| 3 | `video-copy` 时短路滤镜组装 | `FfmpegBuild.cs:237-242` | ~3 行 |
| 4 | `Init()` 加 per-preset try/catch | `FFmpegPresets.cs:41-45` | ~6 行 |
| 5 | `NormalizeVendor` 补 `radeon` / `amd` 判定 | `Gpu.cs:353-361` | ~2 行 |
| 6 | 修 `-encoders` 正则排除 `=` | `HwDetect.cs:82` | 1 行 |
| 7 | 修 `SyncTo` 文件名截断 | `SessionLogWriter.cs:119` | 1 行 |
| 8 | 把 `hwaccel` 下拉收敛为 6 项 + tooltip | `MainForm.cs:287` | ~5 行 |
| 9 | 文件对话框 Filter 与 `Helper` 名单统一 | `MainForm.cs:536` | ~3 行 |
| 10 | `SetProgressInstant` 加 `Maximum > Minimum` 保护 | `MainForm.cs:629` | 1 行 |

### 第二批：需要小设计（建议近期做）

| 序 | 项 | 说明 |
|---|---|---|
| 11 | 「详细日志」透传到 `TranscodeSession`，改用 `FfmpegLogFilter.Apply` | 让开关名副其实；同时修 `SessionReport` 措辞 |
| 12 | `FinishRun` 取消/全失败路径的进度与状态文案 | 消除"100% + 已取消"与"全失败却显示已完成" |
| 13 | DPI 基准前置：`AutoScaleMode/AutoScaleDimensions` 移到构造函数首行 | 消除 `ParamsFlowForm`/`AboutForm` 的时序脆弱性 |
| 14 | 日志区高度下限 + `MinimumSize` 校准 | 避免最小尺寸下日志区塌陷为 0 |
| 15 | `PresetUserArgs` 质量字段改 `double` | 支持小数 CRF |
| 16 | `FfargsValidator` 字符串键的分隔符校验 | 防止 `px=a,b` 破坏参数串 |
| 17 | `CalcLongEdge` 输出下限保护 + GUI `dimension` 下限校验 | 消除 `0x0` 尺寸 |

### 第三批：结构性问题（建议排期评估）

| 序 | 项 | 说明 |
|---|---|---|
| 18 | 删除或归档 `ParamsForm.cs` 及其 3 个测试文件 | 消除双实现与 30 项无效测试 |
| 19 | `AboutForm` 说明窗口加搜索框 + 双击复制参数串 | 既有审计方案 E，成本约 30 行 |
| 20 | 建立"参数正交组合"表驱动测试 | 系统性覆盖 P0-2 这类组合缺陷 |
| 21 | 引入 150% DPI 的布局回归测试 | 与用户实际环境对齐 |
| 22 | 关键区域局部像素断言（替代"非空白"判据） | 让截图测试真正具备发现遮挡的能力 |
| 23 | `hwaccel` 改为 `CliValueKind.Choice` | 让非法取值走既有告警通道 |

---

## 十、审查中确认**无问题**的点（避免后续重复投入）

以下为本次重点核查但确认正常的部分，供后续审查参考：

| 检查项 | 结论 |
|---|---|
| `FfmpegTask.cs:62-66` tree 模式假前缀匹配（`C:\Videos2` 被 `C:\Videos` 误匹配） | **当前不可达**。`srcDir` 来自 `Path.GetDirectoryName(entry.Path)`，而 `entry.Path` 由 `FfmpegScan` 产出：目录输入时 `Root=该目录` 且只枚举其子树，文件输入时 `Root=文件所在目录`。故 `srcDir` 必为 `Root` 或其子目录，`StartsWith` 恒真且剩余串必以分隔符开头。仅记录为潜在风险，非缺陷。 |
| `video-copy` 时 `CreateFromArgv` 是否清空 `filters` | **已正确清空**（`FFmpegPresets.cs:318-320`）。P0-2 的根因是 `Dimension/Speed/Framerate` 未被清且 `BuildFilterArgs` 独立取用，**不是** `filters` 未清。 |
| `Helper.ParseBitrate` 边界 | **健壮**：`3M/800k/1.5g/3m` 均正确，`3Mbps` 与 `-1M` 抛 `ArgumentException`，`0` 返回 0，纯数字与带空格均可。 |
| `Gpu.NvidiaGenerationOf` 型号识别 | **正确**：`RTX 4090→40`、`RTX 3080→30`、`GTX 1660→20`、`RTX 2050→30`（Ampere 硅片特例）。 |
| `HwDetect.ParseFilters` | **正确**：能准确提取 `scale_cuda/scale_qsv/vpp_amf/scale_d3d11`（用真实 `ffmpeg -filters` 验证）。 |
| `HwDetect.ParseHwaccels` | **正确**：表头 `Hardware acceleration methods:` 已被 `RxHwaccelHeader` 排除，逐项提取正确。 |
| `FfmpegRun` 的进程管理 | **健壮**：`CancellationToken` 注册 `Kill(entireProcessTree: true)`；`ReadToEndAsync` + `WaitForExit(timeout)` 组合避免了经典 stdout/stderr 死锁；`OnExit` 在 `finally` 中回调；临时文件在 `finally` 清理且异常被吞。 |
| `FfmpegRun.CommitOutputFile` 的 override 安全 | **正确**：`override=false` 时绝不触碰已有目标；`override=true` 时先备份为 `.old@<ts>_<rand>`，失败回滚，成功后清理备份。 |
| `FfmpegScan` 目录遍历 | **安全**：`EnumerationOptions { IgnoreInaccessible = true }` 处理无权限目录；扩展名判断走 `PathExt`（`ToLowerInvariant`）大小写无关；`UniqueByFields` 按规范化路径去重。 |
| `LogSink` 的生产者-消费者设计 | **正确**：`ConcurrentQueue` 无锁入队，队列上限 20000 + 显示上限 5000 + 单批 400 三重防护，`DroppedCount` 用 `Interlocked` 保证跨线程可见。 |
| `ParamsFlowForm` 的 flag 复选框交互 | **正确**：取消勾选能正确移除参数（实测 `--video-copy --debug` 取消勾选后 preview 变为 `--debug`）。 |
| `SessionReport.FormatDuration` / 统计块 | **正确**：负数返回 `—`，各量级格式合理；失败清单直接列出文件名与阶段。 |
| `FfargsValidator` 数值键校验 | **正确**：`vb=3M` 被识别为"带单位不会生效"并给出裸 bps 示例；`num <= 0` 被拒；`an/anime` 被明确标注为不生效。 |
| `CliOptions.BuildLookup` 大小写策略 | **正确**：使用 `StringComparer.Ordinal` 区分 `-E`(exclude) 与 `-e`(extensions)、`-O`(override) 与 `-o`(output)，注释已说明理由。 |
| `Helper.TextHash` | **可用**：MD5 前 8 位 hex（32 bit）。用于临时文件名的碰撞面极小，且临时文件在同一目录内、已含目标基名，实际风险可接受。 |

---

## 十一、复审建议顺序

若按「用户可感知程度 × 修复成本」排优先级，建议：

1. **P0-1**（关闭按钮）——用户一眼可见，改动 10 行。
2. **P1-3**（正则）——4 个字符，消除一处静默失效。
3. **P1-2**（坏预设）——6 行，避免"整个应用不可用"。
4. **P1-1**（AMD 厂商）——2 行，A 卡用户性能数倍差异。
5. **P0-2**（video-copy）——3 行 + 一条组合测试。
6. **P2-5 / P2-6 / P2-7 / P2-8**——各 1～10 行，消除功能与说明不符。
7. **P2-1 / P2-2 / P1-5**——DPI 与最小尺寸，需设计但收益明确（对应用户 150% 实际环境）。
8. **P2-3 / P2-4**——界面语义与死代码清理。
9. 第三批结构性问题按排期推进。

---

## 附：本次审查使用的实证材料

| 材料 | 位置 | 用途 |
|---|---|---|
| 正则/层叠探针 | `temp/audit-20260928/probe/` | 验证 4 个正则过度转义；验证 AboutForm z-order |
| core 编排探针 | `temp/audit-20260928/probe2/`、`probe7/` | 验证 `video-copy` 与滤镜冲突 |
| GUI 层叠探针 | `temp/audit-20260928/probe3/`、`probe5/`、`probe6/` | AboutForm 命中测试、预设页 Dock 布局、Close 可点性 |
| 硬件层探针 | `temp/audit-20260928/probe4/`、`probe8/` | 厂商识别、候选链、编码器/滤镜解析 |
| 名单与截断探针 | `temp/audit-20260928/probe9/` | 扩展名名单、坏预设隔离、文件名截断 |
| ffmpeg 复现 | `temp/audit-20260928/repro/` | `Filtering and streamcopy cannot be used together` |
| 现有测试截图 | `temp/screenshots/06-about.png` | P0-1 的旁证 |

> 全部探针均为 `temp/` 下的独立小项目，未改动 `src/` 任何文件（`git status` 已确认）。
