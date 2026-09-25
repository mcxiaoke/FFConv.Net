# FFConvNet 简易 GUI 实施方案

> 日期：2026-09-25
> 范围：为 `MediaCli.Transcode`（FFmpeg 转码 core）新增一个简易桌面 GUI
> 前置：本方案不修改 core 的转码语义；GUI 是 core 的一个**薄编排层**
> 关联文档：`README.md`、`docs/PORTING-NOTES.md`、`docs/IMPLEMENTATION-SUMMARY-20260925.md`

---

## 一、目标与边界

### 1.1 目标

一个能**可信地**跑通「选文件 → 选 preset → 加自定义参数 → 看日志 → 出结果」的桌面工具，且：

- 生成的 ffmpeg 命令与 CLI / Node 版**逐字节一致**（不引入第二套参数逻辑）；
- 预览与实际执行**同路径**，预览不会撒谎；
- 出错时用户能看到**真实原因**，而不是"失败了"。

### 1.2 明确不做（v1 边界）

| 不做 | 理由 |
|---|---|
| 并发 / 任务队列 / 暂停续传 | core 有意省略了 `ffmpeg_engine.js`（Engine 编排层）；core 是同步模型 + 进程内静态缓存，NVENC/QSV 并发还会争显存 |
| 裸 ffmpeg 参数注入框 | 会绕过 preset 体系与 hwaccel 分层，破坏与 Node 版的 parity |
| 预设编辑器 | 改 YAML 请直接编辑 `~/.mediac/presets.yaml` |
| 拖拽、托盘、多语言、主题 | 非目标 |
| 任务历史 / 断点恢复 | 非目标 |

---

## 二、选型

| 项 | 选择 | 理由 |
|---|---|---|
| UI 框架 | **WinForms**（`net8.0-windows`） | 本机 `Microsoft.WindowsDesktop.App.Ref` 8.0.18 / 8.0.30 已就绪；SDK 自带模板；**零额外 NuGet 依赖**；纯代码布局，无 XAML 心智负担。Avalonia 需引入 8 个包，WPF 的 XAML 对「几个按钮 + 一个日志框」过重 |
| 与 core 的关系 | **ProjectReference 引用 `MediaCli.Transcode`** | 转码逻辑一行都不复制 |
| 依赖方向 | GUI → Core（单向） | Core 不知道 GUI 存在；CLI 不受影响 |

### 2.1 必须由 GUI 补上的唯一逻辑

core 有意省略了 `ffmpeg_engine.js`，因此「收集输入 → 逐文件 prepare → 执行」这个循环必须由 GUI 层补上（约 40 行）。这是**唯一**新增的编排逻辑，其余全部复用既有公开 API。

---

## 三、目标框架与运行时兼容

按需求，基础 TFM 降到 **`net8.0`**，同时配置成可在 .NET 10 / 12 运行时上直接跑。

### 3.1 配置

`Directory.Build.props`（仓库根，对所有项目生效）：

```xml
<TargetFramework>net8.0</TargetFramework>
<RollForward>Major</RollForward>
```

GUI 项目显式覆盖为 `net8.0-windows`。

### 3.2 `RollForward=Major` 的语义

写入 `runtimeconfig.json` 的 `rollForward` 字段：

- 目标框架依赖 `.NET 8` 运行时；
- 若机器上**没有** 8.x，则前滚到**最低的更高主版本**（如 10.0.11）——这正是"兼容 10 / 12"的诉求；
- 若机器上有 8.x，则**仍用 8.x**，行为不变、可预期。

**为什么不用 `LatestMajor`**：它会无条件挑最高版本，导致「装了 10 就永远不用 8」，破坏可预期性。`Major` 是"仅在缺失时前滚"，更稳。

### 3.3 兼容性边界（必须如实说明）

前滚只保证**运行时可用**，不保证新运行时的行为差异：

- .NET 8 编译的程序集在 .NET 10 运行时上受 .NET 10 的**行为变更**影响（GC、线程池、正则引擎、`Process` 细节）；
- 本项目的进程调用依赖 `ProcessStartInfo.ArgumentList`（core 的既有约束：禁止 shell 拼接），该 API 在 8/10/12 上语义稳定；
- `AppContext.BaseDirectory` 路径探测逻辑（`PresetLoader.FindBundledPresetPath`）与运行时版本无关。

因此：**`net8.0` 为编译基线，10/12 为可运行目标**；实际发布若锁定单一运行时，建议显式指定。

---

## 四、目录与文件结构

### 4.1 预设文件归位

`default.yaml` 从 `src/MediaCli.Transcode/Assets/` 移到仓库根 **`presets/default.yaml`**：

- 与 `README.md` 描述的仓库结构一致（`dotnet/presets/default.yaml`）；
- 与上游 `media-cli.js/presets/default.yaml` 目录约定一致（单一事实源）；
- 与 csproj 原本的引用意图一致（`..\..\presets\` = 从 `src/MediaCli.Transcode/` 上溯到仓库根）。

csproj 引用修正为：

```xml
<None Include="..\..\presets\default.yaml" Link="presets\default.yaml" CopyToOutputDirectory="PreserveNewest" />
```

> 修复说明：原引用 `..\..\..\presets\default.yaml` 解析到 `C:\Home\Projects\presets\`，该路径不存在，导致 `dotnet build` 报 `MSB3030` 失败。这是**既有缺陷**，非 GUI 引入。

### 4.2 新增 GUI 项目

```
src/MediaCli.Transcode.Gui/
├── MediaCli.Transcode.Gui.csproj   # net8.0-windows + UseWindowsForms
├── Program.cs                      # [STAThread] 入口 + 全局异常兜底
├── MainForm.cs                     # 布局与事件绑定（纯代码，无设计器）
├── GuiOptions.cs                   # ★ UI 选项 → ArgvShim / ArgvOptions / TaskDeps 的纯映射
├── TranscodeSession.cs             # ★ 串行编排循环 + 取消 + 回调 marshal（不含 UI 类型）
└── LogSink.cs                      # 环形缓冲 + 定时批量刷入
```

`GuiOptions` 与 `TranscodeSession` 刻意**不引用任何 WinForms 类型**，因此可直接写 xUnit 测试——这是"可信"的落点。

---

## 五、界面布局

```
┌ 输入 ──────────────────────────────────────────────┐
│ [路径输入框..........................] [选文件][选目录] │
│ 已收集 12 个文件（可展开列表）                          │
├ 参数 ──────────────────────────────────────────────┤
│ Preset [hevc_2k ▼]   hwaccel [auto ▼]   decode [auto ▼]│
│ 输出目录 [.............................] [浏览]        │
│ 输出模式 (•)dir  ( )tree  ( )file                     │
│ 自定义参数 [vb=3M,vq=23,sp=1.0...........]             │
│ ☐覆盖已有  ☐严格模式  ☐详细日志  ☐动漫模式              │
├ 操作 ──────────────────────────────────────────────┤
│ [预览命令] [开始转码] [取消] [清空日志] [打开输出目录]   │
├ 日志 ──────────────────────────────────────────────┤
│ ┌ RichTextBox 只读、自动滚动、上限 5000 行 ┐            │
│ └──────────────────────────────────────┘            │
│ 进度 [████████░░░░░░] 62%   2.4x                    │
└────────────────────────────────────────────────────┘
```

**「自定义参数」输入框走 `--ffargs` 白名单**（`FFmpegPresets.ParseFfargs` + `ApplyFfargs`），支持 `vb / vbit / vq / vc / ab / aq / ac / px / sx / sp / dm / fps / md / an` 等别名，全部有既有单元测试覆盖。

---

## 六、数据流与映射

```
UI 控件
  → GuiOptions（纯函数）
      ├─ ArgvShim    → FFmpegPresets.CreateFromArgv()   ← 预设 + 覆盖
      ├─ ArgvOptions → entry.Argv                       ← override/strict/debug/anime/decodeMode
      └─ TaskDeps    → FfmpegTask.BuildCliTask()        ← output / outputMode
  → FfmpegScan.CollectInputFiles() + FilterAndSliceEntries()
  → 每个文件 TranscodeEntry{ Preset = preset.Clone(), TestMode = !doit }
  → FfmpegTask.BuildCliTask()      ← ffprobe 媒体信息、目标参数、目标路径
  → FfmpegRun.RunFFmpeg(entry, RunOptions{ OnLog, OnProgress, Signal })
  → entry.ToRunResult()            ← 稳定结果投影
```

逐条对应 CLI `Program.cs` 的 `BuildArgv` / `BuildShim` / `RunPlan`，语义完全同构。

### 6.1 关键映射细节

- **preset 名**：直接传给 `CreateFromArgv`，别名（`anime`/`av1`/`h264`/`hevc_anime`…）由 core 解析，GUI 不重复实现。
- **hwaccel**：**原样透传，包括 `auto`**。经实测（见 §11.1），`auto` 模式下传 `"auto"` 与传 `null` 产生的候选链**完全相同**，因此无需改写；且 `decodeMode=gpu` 时 `"auto"` 是「用默认硬件层」的合法语义，改写反而会误伤。GUI 只把空白归一为 `null`。
- **decodeMode=cpu**：`CandidateTiers` 直接返回 `["cpu"]`，可作"强制软解"逃生口。

### 6.2 ffargs 输入框的真实契约（实测校准）

`--ffargs` 是**最容易踩坑**的入口，`FfargsValidator` 专门为此存在。三条经 node 跑 JS 原版实测确认的约束：

| 约束 | 事实 | GUI 处置 |
|---|---|---|
| **码率必须写裸 bps** | `applyFfargs` 要求 `typeof value === "number"`，而 `parseValue("3M")` 返回**字符串** → 被静默丢弃。带单位写法（"233k"/"4M"）只适用于 **YAML 预设** | 拦截 `vb=3M`，提示改 `vb=3000000` |
| **分隔符不一致** | JS `arg_parser` 用 `;`/`:`/`#`；C# `ParseFfargs` 用 `,`。实测 `vb=3M,vq=23` 在 JS 侧被当成**一个值** | 同时接受 `,` 与 `;`，统一归一为 C# 形式 |
| **`an`/`anime` 是空操作** | `ARG_ALIASES` 里有这两个别名，但 `applyFfargs` **没有对应分支** → 声明了却不生效 | 告警并指向「动漫模式」复选框 |

**设计原则：凡是不能生效的输入，一律给出明确警告，绝不静默吞掉。** 未知键、非法数字、非正值同样告警。

---

## 七、可靠性设计要点

### 7.1 预览 = 实际，绝不偏差

「预览命令」**不是**走 CPU 占位（CLI `plan` 那样），而是 `RunFFmpeg(entry, TestMode=true)`：

1. 它**先**完成真实的 `ResolveHwPlan`（真探测硬件层）与 `CreateFfmpegArgs`；
2. 再在 `TestMode` 处提前返回。

所以从 `entry.FFmpegArgs` 拿到的命令与真实执行**逐字节一致**。

**必须处理的哨兵值**：`TestMode` 分支会设 `FFmpegFailed = true; FFmpegError = "test-mode skip"`。UI 必须识别这个哨兵并按「预览成功」处理，否则每个文件都会显示成红色失败。

**非破坏性保证**：`TestMode` 的 `return` 位于 `Directory.CreateDirectory` 与 `File.Exists(FileDst)` 检查**之前**，因此预览不建目录、不碰文件。`FfmpegTask.BuildCliTask` 只做只读探测。

**显示口径**：预览显示 `ffmpeg {FlattenFFArgs(entry.FFmpegArgs)}`，与 `mediac-dotnet plan` 的输出格式一致。真实执行时 core 还会额外注入 `-metadata comment=mediac <该命令>`（`FfmpegRun.GetCommentArgs`，1000 字符截断）。GUI 不复制这段私有逻辑，避免逻辑漂移——此差异在界面文案与文档中如实标注。

### 7.2 UI 线程安全：队列 + 定时器，不用高频 Invoke

`OnLog` 从 `ErrorDataReceived` 的线程池线程触发。直接 `Invoke` 会因同步等待导致卡死/严重降速。

做法：`ConcurrentQueue<string>` 入队（生产者无锁无等待）→ WinForms `Timer` 每 100ms drain 一次刷入 RichTextBox。进度同理走 `volatile` 字段 + 同一 Timer。

### 7.3 后台执行

`FfmpegRun.RunFFmpeg` 是同步的，内部 `proc.WaitForExit(Timeout.Infinite)`。必须整体放到后台线程（`Task.Run`），否则 UI 冻死。

### 7.4 串行，不并发

core 是同步模型 + 静态缓存（probeCache / `HwDetect.CachedCapabilities` / `FfmpegTask.CachedCaps`），并发会互相污染且争显存。v1 严格串行，这也是 CLI 的既有行为。

### 7.5 取消链路

`CancellationTokenSource` → `RunOptions.Signal` → `RunFFmpeg` 内部 `Token.Register` kill 整个进程树 → `ThrowIfCancellationRequested` → 识别为 `Cancelled`。

**已知盲区（如实标注）**：`HwDetect.DetectHardwareCapabilities` 的探测子进程不接收 token，探测期间（首次约数秒）取消会延迟生效。

### 7.6 关窗保护

`FormClosing` 时若任务在跑：弹确认 → 取消 → 等待进程退出。否则 ffmpeg 变孤儿进程继续占 GPU。

### 7.7 日志不撑爆内存

`-v error` 时日志很少；勾选「详细日志」会切到 `repeat+level+info`，量级暴涨。三重防护：环形缓冲 + 5000 行上限 + 批量刷新。

### 7.8 可诊断性

启动时 `FfmpegRun.SetFFmpegPath(FfmpegBin.ResolveFFmpegBinary())`，并把解析结果、版本、主 GPU、可用硬件层显示在日志区首屏。用户永远知道「实际在用哪个 ffmpeg、走的哪一层」。

### 7.9 默认安全

默认 `Override = false`（不覆盖已有产物），与 core 契约一致；真正执行需显式点「开始转码」。

---

## 八、验证计划

| # | 验证项 | 手段 |
|---|---|---|
| 1 | 构建 | `dotnet build` 零错误；既有 **59 项测试全绿**（防回归） |
| 2 | 映射等价 | 新单测：同一组 UI 输入 → `ArgvShim` → 生成的完整命令，与 CLI 同参数输出**逐字节比对** |
| 3 | 预设加载 | `presets/default.yaml` 归位后预设数不变（29 个可用 + 5 个 `_base_*` 不注册） |
| 4 | 预览可信 | 手工对比「预览命令」输出 vs `mediac-dotnet plan` 输出 |
| 5 | 端到端 | 用 `TEST2__*` 素材真跑：产物经 ffprobe 校验、进度推进、日志滚动 |
| 6 | 取消 | 执行中取消，确认进程树被杀、无残留 `_tmp@*@tmp_` 文件 |
| 7 | 边界 | 目标已存在（跳过）、坏文件（bad_format）、音频预设、10bit→h264 位深对齐 |
| 8 | 运行时前滚 | 检查 `runtimeconfig.json` 的 `rollForward` 字段为 `Major` |

---

## 九、实施步骤（已全部完成）

1. ✅ **修既有缺陷**：`presets/default.yaml` 归位到仓库根 + csproj 引用修正 → 构建恢复。
2. ✅ **降 TFM**：`Directory.Build.props` 改 `net8.0` + `RollForward=Major`。
3. ✅ **落映射层**：`GuiOptions.cs`、`FfargsValidator.cs` + 33 项单测（不碰 UI）。
4. ✅ **搭界面**：`TranscodeSession.cs`、`LogSink.cs`、`MainForm.cs`、`Program.cs`、`app.manifest`。
5. ✅ **接编排**：串行循环 + 取消 + 进度 + 日志批量刷新。
6. ✅ **端到端验证**：真机转码 + 取消 + 边界（详见 §11）。
7. ✅ **文档**：本文件、`README.md`、`.gitattributes`、`.editorconfig`、`docs/CHANGES-20260925.md`。

---

## 十、实测校准（推翻初版方案假设的部分）

方案初版有若干**基于推断**的结论，实施中用 node 跑 JS 原版逐条核实，发现两处错误。如实记录，以免后续按错误认知修改。

### 10.1 「`auto` 会锁死候选链」——**错误**

初版声称：GUI 必须把 UI 的 `auto` 转成 `null`，否则 `normalizeHwaccelName("auto")` 会归一成 `"d3d"`，把候选链锁死成 `[d3d, cpu]`。

实测（node 跑 JS 原版）：

```
decodeMode=auto hwaccel=auto      -> ["cuda","d3d","cpu"]   ← 厂商层在场
decodeMode=auto hwaccel=undefined -> ["cuda","d3d","cpu"]   ← 完全相同
decodeMode=gpu  hwaccel=auto      -> ["d3d"]                ← 合法语义
decodeMode=gpu  hwaccel=undefined -> 抛错 requires --hwaccel
```

原因：`candidateTiers` 在 auto 分支**先于** `normalizeHwaccelName` 拦截了 `"auto"`（JS `hwdetect.js:544`；C# `HwDetect.cs:319` 有同样的守卫）。真正分道的是 `decodeMode=gpu`。

**结论：GUI 原样透传，不做改写。** 由 `AutoMode_AutoEqualsNull_ForCandidateChain` 与 `GpuMode_AutoIsAccepted_NullThrows` 锁定。

### 10.2 「`vb=3M` 可用」——**错误**

初版示例写了 `vb=3M`。实测该写法**完全不生效**：`parseValue("3M")` 返回字符串，而 `applyFfargs` 对码率要求 `typeof value === "number"` → 静默丢弃（无任何警告）。正确写法是裸 bps：`vb=3000000`。

这正是 `FfargsValidator` 存在的理由——把静默失败变成明确告警。

### 10.3 stderr 编码：确认**非**缺陷

实测 ffmpeg 的 stderr 是 **UTF-8**（中文路径字节 `e4 b8 8d...`），而 core 只给 stdout 设了 Latin1。

进一步实测：**.NET 8 的 stderr 默认解码已是 UTF-8**，中文路径本就能正确显示——初版的"中文乱码"担忧不成立。

仍显式加 `StandardErrorEncoding = Encoding.UTF8`，理由是不依赖宿主控制台状态（GUI 由资源管理器启动与终端启动的码页可能不同），属防御性固定而非缺陷修复。

### 10.4 `ToEven` 测试数据错误（顺带修正）

`HwAccelTests.ToEven_RoundsLikeFfmpeg` 的 InlineData 写 `607.5→606`、`569.53→568`，与同文件 `ToEven_Exact` 的 `608/570` **自相矛盾**，且参数从未被使用（xUnit1026 警告，在 `TreatWarningsAsErrors=true` 下成为错误）。

实测 C# 与 JS 均为 `608 / 570`，按实测值修正并让断言真正使用参数。

---

## 十一、行尾与格式约定

本仓库此前**行尾混用**（21 个 LF、10 个 CRLF）。后果：编辑器一次保存就产生"整文件全改"的虚假 diff——实施中 `FFmpegPresets.cs` 只加 7 行，却被报成 735 行改动。

### 11.1 选择：仓库内统一 LF

C# 没有唯一"官方"行尾，两种都合规：

- Microsoft 的 `.editorconfig` 模板默认 `[*.cs] end_of_line = crlf`（面向 Windows/VS 的传统默认）；
- 现代 .NET 生态主流是「仓库内 LF + `*.cs text eol=lf diff=csharp`」（dotnet/runtime 等）。

选 LF：跨平台一致、diff 干净；Windows 上仍可用 CRLF 工作副本（Git 负责归一）。

### 11.2 落地

- **`.gitattributes`**：`* text=auto eol=lf`、`*.cs text eol=lf diff=csharp`、XML 类 LF、`*.bat`/`*.cmd` 保持 CRLF、二进制禁转换。
- **`.editorconfig`**：`end_of_line = lf`、`charset = utf-8`、C# 4 空格、XML 类 2 空格、Markdown 保留行尾空格。

刻意**不**在 `.editorconfig` 里声明分析器严重级别——`TreatWarningsAsErrors=true` 下随意提升规则会直接让构建失败。

---

## 十二、实施结果（2026-09-25）

| 验证项 | 结果 |
|---|---|
| 构建 | **0 错误 0 警告**（`TreatWarningsAsErrors=true`） |
| 测试 | **92/92 通过**（原 59 项 + 新增 33 项 GUI 映射测试） |
| 映射等价性 | 同一 hwPlan 下 CLI 映射与 GUI 映射命令**逐字节一致** |
| 预览可信 | GUI 预览解析出真实硬件层 **cuda**（`hevc_nvenc -rc vbr -tune hq -cq 28`），而非 CLI plan 的 cpu 占位 |
| 预览安全 | 目标文件、临时文件、输出目录**均未创建** |
| 真实转码 | hevc 1920x1080 10bit + aac 48k，无临时文件残留 |
| 位深对齐 | `yuv420p10le` → `yuv420p`（10bit 源 → h264 目标） |
| 音频预设 | `aac_medium` 正常，输出 `_128K.m4a` |
| 目标已存在 | 正确 `skip[destination_exists]` |
| 取消链路 | 识别为取消、进程终止、**零残留**（实测 2767ms > 1200ms，印证探测期不可中断） |
| 运行时前滚 | `runtimeconfig.json` 的 `rollForward = Major` 已生效 |

---

## 十三、遗留限制（如实标注）

| 限制 | 说明 |
|---|---|
| 探测期不可中断 | `HwDetect.DetectHardwareCapabilities` 的子进程不接收 token，首次探测（约 2–3 秒）期间取消会延迟生效。UI 已给出对应提示文案 |
| 注释元数据未在预览显示 | 真实执行时 core 会注入 `-metadata comment=mediac <命令>`（1000 字符截断，`FfmpegRun.GetCommentArgs`）。GUI 不复制这段私有逻辑以免漂移，故预览命令与该注释存在已知差异 |
| 测试项目为 Windows TFM | 测试项目引用 GUI（`net8.0-windows`），故整体成为 Windows-only。被测的 `GuiOptions`/`TranscodeSession` 本身不含 WinForms 类型，若将来需要跨平台测试，可把映射层拆为独立 `netstandard2.0` 类库 |
| `RollForward` 适用范围 | 仅对**框架依赖**部署生效；若将来做 self-contained / AOT 发布，该设置无意义 |
| AMF 路径未实拍 | 本机无 A 卡，AMD 分支沿用 core 既有结论（core 亦标注未实拍验证） |
