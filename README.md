# MediaCli.Transcode — C#/.NET 10 移植版

本目录是 media-cli.js 中 `src/transcode`（FFmpeg 转码领域核心）的 C# 重新实现，
基于 **.NET 10**（SDK 10.0.400），核心功能与 Node 版保持一致，不做 100% 还原。

## 结构

```
dotnet/
├── MediaCli.Transcode.sln
├── src/
│   ├── MediaCli.Transcode/                 # 核心类库（net10.0）
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
│   └── MediaCli.Transcode.Cli/             # CLI（mediac-dotnet）：presets / plan / run
├── tests/MediaCli.Transcode.Tests/         # xUnit 测试（59 项）
└── docs/PORTING-NOTES.md                   # 移植要点与已知差异
```

## 构建 / 测试 / 运行

```bash
cd dotnet
dotnet build                     # 全解决方案
dotnet test                      # 59 项测试

# CLI（默认 dry-run，--doit 才真正执行——与 mediac ffmpeg 的契约一致）
dotnet run --project src/MediaCli.Transcode.Cli -- presets
dotnet run --project src/MediaCli.Transcode.Cli -- plan --input <文件或目录> --preset hevc_2k [--output DIR]
dotnet run --project src/MediaCli.Transcode.Cli -- run  --input <文件或目录> --preset hevc_2k --doit
```

环境要求：.NET 10 SDK；ffmpeg/ffprobe（`FFMPEG_PATH`/`FFPROBE_PATH` 或 PATH）。

## 已验证

- **预设**：内置 default.yaml 30 个预设全部正确加载（含 extends 继承、_override 保护）。
- **单元测试**：59/59 通过（尺寸计算/质量偏移/编码器参数块/滤镜链/ffargs/解析器/候选层链/GPU 矩阵…）。
- **真实转码**（RTX 4070 + N-126733 ffmpeg，`data/videos/TEST2__*`）：
  - `hevc_2k` ← hevc 10bit 源：成功，产物 hevc 1920x1080 10bit + aac；
  - `h264_2k` ← h264 4:2:2 8bit 源：成功，产物 h264 1920x1080 + aac。
- **命令等价性（parity）**：对同一构造输入，JS 版 `createFFmpegArgs` 与 C# 版
  生成的完整命令行**逐字节一致**（见 docs/PORTING-NOTES.md 附录）。

详细的移植对照与已知差异见 `docs/PORTING-NOTES.md`。
