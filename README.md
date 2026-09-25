# MediaCli.Transcode — C#/.NET 移植版

本目录是 media-cli.js 中 `src/transcode`（FFmpeg 转码领域核心）的 C# 重新实现，
**编译基线 .NET 8**（缺失 8.x 运行时按 `RollForward=Major` 前滚到 10 / 12），
核心功能与 Node 版保持一致，不做 100% 还原。

## 结构

```
FFConvNet/
├── presets/default.yaml                    # 内置预设（唯一事实源，随包发布）
├── MediaCli.Transcode.slnx
├── src/
│   ├── MediaCli.Transcode/                 # 核心类库（net8.0）
│   │   ├── Presets/                        # ← preset_schema.js / preset_loader.js / ffmpeg_presets.js
│   │   │   ├── PresetSchema.cs             #   字段白名单 + 类型校验（单一事实源）
│   │   │   ├── PresetLoader.cs             #   YAML 分层加载 / extends 继承 / _override 合并
│   │   │   └── FFmpegPresets.cs            #   预设注册表 / createFromArgv / ffargs 别名
│   │   ├── Hardware/                       # ← gpu.js / hwdetect.js / hwaccel.js
│   │   │   ├── Gpu.cs                      #   NVIDIA 代次解析 + NVENC/NVDEC 支持矩阵
│   │   │   ├── HwDetect.cs                 #   设备级能力探测（-encoders/-hwaccels/-filters）
│   │   │   └── HwAccel.cs                  #   分层矩阵 / 质量归一化 / 滤镜与编码器参数 / selectTier
│   │   ├── Planning/FfmpegPlan.cs          # ← ffmpeg_plan.js（目标参数纯计算）
│   │   ├── Build/FfmpegBuild.cs            # ← ffmpeg_build.js（命令行三段拼装）
│   │   ├── MediaInfo/FfprobeMediaInfo.cs   # ← lib/mediainfo.js（ffprobe JSON 路径）
│   │   ├── Scan/FfmpegScan.cs              # ← ffmpeg_scan.js（输入收集/过滤/切片）
│   │   ├── Run/                            # ← ffmpeg_task.js / ffmpeg_run.js / ffmpeg_result.js
│   │   │   ├── FfmpegTask.cs               #   prepare 阶段任务构建（跳过判定/目标路径/字幕）
│   │   │   └── FfmpegRun.cs                #   执行：硬件分层决策→构建→进度→提交/恢复
│   │   ├── FfmpegBin.cs                    # ← ffmpeg_bin.js（ffmpeg/ffprobe 定位）
│   │   ├── Model/                          # 共享契约：MediaInfo / Preset / Hardware / Entry
│   │   └── Support/                        # lib/core.js + lib/helper.js 的领域内子集
│   ├── MediaCli.Transcode.Cli/             # CLI（mediac-dotnet）：presets / plan / run
│   └── MediaCli.Transcode.Gui/             # 桌面 GUI（mediac-gui，net8.0-windows / WinForms）
│       ├── GuiOptions.cs                   # ★ UI 选项 → ArgvShim/ArgvOptions/TaskDeps 纯映射
│       ├── FfargsValidator.cs              # ★ ffargs 解析校验（拦截静默失效的写法）
│       ├── TranscodeSession.cs             # ★ 串行编排 + 取消（core 未含 Engine 层）
│       ├── LogSink.cs                      #   无锁队列 + 批量刷新
│       └── MainForm.cs                     #   界面（纯代码布局）
├── tests/MediaCli.Transcode.Tests/         # xUnit 测试（92 项）
├── docs/PORTING-NOTES.md                   # 移植要点与已知差异
├── docs/GUI-DESIGN-20260925.md             # GUI 方案与实测校准
├── .gitattributes                          # 行尾统一为 LF
└── .editorconfig                           # 代码风格
```

## 构建 / 测试 / 运行

```bash
dotnet build                     # 全解决方案（0 错误 0 警告）
dotnet test                      # 92 项测试

# CLI（默认 dry-run，--doit 才真正执行——与 mediac ffmpeg 的契约一致）
dotnet run --project src/MediaCli.Transcode.Cli -- presets
dotnet run --project src/MediaCli.Transcode.Cli -- plan --input <文件或目录> --preset hevc_2k [--output DIR]
dotnet run --project src/MediaCli.Transcode.Cli -- run  --input <文件或目录> --preset hevc_2k --doit

# GUI
dotnet run --project src/MediaCli.Transcode.Gui
```

环境要求：.NET SDK（编译基线 **net8.0**，运行时缺失 8.x 时按 `RollForward=Major` 前滚到 10 / 12）；
ffmpeg/ffprobe（`FFMPEG_PATH`/`FFPROBE_PATH` 或 PATH）。

### GUI 用法

输入框每行一个路径（目录会递归扫描）→ 选 preset / hwaccel / 解码模式 → 可选填自定义参数 →
**先点「预览命令」**（解析真实硬件层，显示与执行一致的命令行，不写盘）→ 再点「开始转码」。

自定义参数走 `--ffargs` 白名单，别名如 `vb`(码率) `vq`(质量) `vc`(视频编码器) `ab` `aq` `ac`
`px`/`sx`(前后缀) `sp`(速度) `dm`(尺寸) `fps`(帧率) `md`(元数据)。

> ⚠️ 码率必须写**裸 bps**（`vb=3000000`）。`vb=3M` 这类带单位写法只适用于 YAML 预设，
> 在 ffargs 里会被 core 静默丢弃——GUI 会拦截并给出提示。
> 动漫模式请用复选框（ffargs 的 `an`/`anime` 在 core 中未实现，属空操作）。

## 已验证

- **预设**：内置 default.yaml 34 个条目（29 个可用 + 5 个 `_base_*` 不注册）全部正确加载（含 extends 继承、`_override` 保护）。
- **单元测试**：**92/92** 通过（尺寸计算/质量偏移/编码器参数块/滤镜链/ffargs/解析器/候选层链/GPU 矩阵/GUI 映射等价性…）。
- **真实转码**（RTX 4070 + ffmpeg 8.1.2）：
  - `hevc_2k` ← hevc 10bit 源：成功，产物 hevc 1920x1080 **10bit** + aac 48k；
  - `h264_2k` ← hevc 10bit 源：成功，产物 h264 1920x1080 **8bit**（位深对齐 `yuv420p10le`→`yuv420p`）；
  - `aac_medium` ← m4a 源：成功，输出 `_128K.m4a`。
- **GUI 映射等价性**：同一 hwPlan 下，CLI 映射与 GUI 映射生成的命令**逐字节一致**。
- **GUI 预览**：解析真实硬件层（实测 `cuda` → `hevc_nvenc -rc vbr -tune hq -cq 28`），
  且不创建目标文件/临时文件/输出目录。
- **取消**：进程树被终止、无 `_tmp@*@tmp_` 残留。
- **命令等价性（parity）**：对同一构造输入，JS 版 `createFFmpegArgs` 与 C# 版
  生成的完整命令行**逐字节一致**（见 docs/PORTING-NOTES.md 附录）。

详细的移植对照与已知差异见 `docs/PORTING-NOTES.md`；GUI 方案与实测校准见 `docs/GUI-DESIGN-20260925.md`。
