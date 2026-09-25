# src/transcode → C#/.NET 10 移植实施总结

> 日期：2026-09-25
> 范围：`src/transcode`（FFmpeg 转码领域核心）→ `dotnet/src/MediaCli.Transcode`（.NET 10 / C# latest）
> 关联文档：`dotnet/README.md`（使用说明）、`dotnet/docs/PORTING-NOTES.md`（移植要点）、`docs/FFMPEG-USAGE.md`（Node 版契约）

## 一、总体结论

- Node 版 `src/transcode` 六大核心链路（预设加载、硬件探测与分层、目标参数计算、命令行拼装、单文件执行、二进制定位）已全部有等价 C# 实现。
- **验证全部通过**：`dotnet build` 零错误；单元测试 **59/59**；真机两路转码成功（hevc 10bit→hevc_2k、h264 4:2:2→h264_2k，产物经 ffprobe 确认）；JS 与 C# 对同一输入生成的完整 ffmpeg 命令 **逐字节一致**（390 字符 parity 验证）。
- Node 版代码零改动，仍为唯一行为基准。

## 二、已实现清单

### 2.1 模块对照（19 个 C# 源文件）

| Node 版 | C# 版（dotnet/src/MediaCli.Transcode/） | 状态 |
|---|---|---|
| preset_schema.js | Presets/PresetSchema.cs | ✅ 完整 |
| preset_loader.js | Presets/PresetLoader.cs | ✅ 完整 |
| ffmpeg_presets.js | Presets/FFmpegPresets.cs | ✅ 完整 |
| gpu.js | Hardware/Gpu.cs | ✅ 完整（探测方式有差异，见 §4.1） |
| hwdetect.js | Hardware/HwDetect.cs | ✅ 完整 |
| hwaccel.js | Hardware/HwAccel.cs | ✅ 完整（核心） |
| ffmpeg_plan.js | Planning/FfmpegPlan.cs | ✅ 完整（readMusicMeta 见差异） |
| ffmpeg_build.js | Build/FfmpegBuild.cs | ✅ 完整（核心） |
| ffmpeg_run.js | Run/FfmpegRun.cs | ✅ 完整（核心） |
| ffmpeg_task.js | Run/FfmpegTask.cs | ✅ 完整 |
| ffmpeg_scan.js | Scan/FfmpegScan.cs | ✅ 完整（collectInputFiles + 过滤切片） |
| ffmpeg_result.js | Model/TranscodeEntry.cs `ToRunResult()` | ✅ 完整 |
| ffmpeg_bin.js | FfmpegBin.cs | ✅ 完整 |
| lib/mediainfo.js（ffprobe 路径） | MediaInfo/FfprobeMediaInfo.cs | ✅ ffprobe JSON 路径 |
| lib/core.js / lib/helper.js（领域子集） | Support/Core.cs、Support/Helper.cs | ✅ 子集 |
| ffmpeg_engine.js / ffmpeg_events.js | — | ❌ 未实现（见 §3） |
| ffmpeg_options.js | — | ❌ 未实现（WebUI 选项归一化） |
| ffmpeg_plan_snapshot.js | — | ❌ 未实现（快照信封） |
| cmd/cmd_ffmpeg.js 编排层 | MediaCli.Transcode.Cli（最小实现） | ⚠️ 部分 |

### 2.2 已实现的关键语义（均有单元测试或真机验证背书）

- **预设**：YAML 分层（包内 default.yaml → ~/.mediac → cwd）、`extends` 递归合并 + 循环检测、同名覆盖必须 `_override: true`、字段白名单/类型校验、码率字段 `parseBitrate` 归一（"233k"/"4M"/1000 进制）、`createFromArgv` 别名映射（anime→hevc_2k 等）、`applyFfargs` 数值 0 视为未提供的优先级修复。
- **硬件分层（S-4）**：TIERS 六层（cuda/qsv/amf/d3d/swdec/cpu）逐项一致；ENCODER_MATRIX；候选链 `厂商层 → swdec（有厂商硬件编码器才入场）→ d3d → cpu`；auto+显式 `--hwaccel` 白名单过滤；`normalizeHwaccelName("auto")="d3d"` 老陷阱拦截；GPU 矩阵预筛只拦「明确 no」。
- **质量归一化**：QUALITY_OFFSET 三段阶梯（avc_nvenc [5,4,1] 等）、VMAF_QUALITY_OFFSET（hw-h264 +7 / hw-hevc +5）、按「实际编码器实现」分发（swdec 必需）。
- **编码器参数块**：nvenc（-rc vbr -tune hq -rc-lookahead 30；CQ=-cq+-b:v 0）、qsv（-global_quality）、amf（-rc qvbr/vbr_peak）、cpu（-crf -preset medium；av1/vp9 CQ 配 -b:v 0）；动漫参数四组；CQ+显式 maxBitrate 峰值封顶（amf 只发 -maxrate）；运行时编码器回退（ENCODER_RUNTIME_FALLBACK）。
- **位深对齐**：10bit 源 + h264 目标 → cuda/qsv 走 scale `:format=nv12`、swdec 走 `-pix_fmt yuv420p`；位深未知按「需要对齐」保守处理；探测与真实命令共用同一 buildVideoFilters/buildEncoderArgs（同构）。
- **探测**：`-frames:v 10 -f null -`、只看退出码、带超时、缓存键含位深/显式编码器/speed/framerate/anime、**只缓存成功**。
- **目标参数计算**：音频 smartBitrate 阶梯表、无损兜底 999k、48k 默认、不高于源；分辨率码率幂律 `^0.75`（锚点=源按目标长边等比理论尺寸）；动漫质量自适应（av1 +4 / 其余 +2）；帧率 2% 容差；长边禁止放大 + 四舍五入偶数。
- **命令拼装**：三段结构（input/middle/output）；MP4/MKV 字幕模板与 PGS 降级 `-sn`；外挂字幕挂载；软解层 CUDA 滤镜前置 `hwupload_cuda,`；音频 copy 三条件（含容器兼容黑名单）与 speed≠1 强制重编码；MKV 过期统计标签清理（12 参数）；用户 --metadata 后写覆盖；comment 写入完整命令行（1000 字符截断）。
- **执行**：默认 dry-run 契约；`-progress -` 的 out_time/speed 进度解析；错误提取三级策略（首条 [error] 行 → 特征词行 → 兜底末行，含噪声行过滤）；产物安全（不覆盖、override 先备份后回滚、>1MB 源 + ≤20KB 产物判定不进重试链路）；临时文件 `_tmp@hash@tmp_` 命名与清理。
- **二进制定位**：FFMPEG_PATH → FFMPEG_BINARY → PATH；ffprobe 额外支持 ffmpeg 同目录回退；环境变量值「实际存在才采用」。

## 三、未实现清单（含理由）

| 项 | 类别 | 说明 |
|---|---|---|
| ffmpeg_engine.js（Engine 任务编排/并发） | 有意省略 | 属 WebUI/桌面共享编排层；CLI 端按串行执行实现，需要并发时可在此基础上包一层 |
| ffmpeg_events.js（事件总线） | 有意省略 | 与 Engine 配套 |
| ffmpeg_options.js（WebUI/Cli 选项归一化） | 有意省略 | CLI 直接解析自己的参数；ffargs 解析/别名已保留在 FFmpegPresets |
| ffmpeg_plan_snapshot.js（公开快照信封） | 有意省略 | 仅供 GUI 传输层使用 |
| cmd_ffmpeg.js 的交互确认、汇总表、删除源文件、CPU 重试链 | 部分 | C# CLI 覆盖 presets/plan/run 主干；交互确认类能力按「无人值守 CLI」设计省略 |
| 进度条 UI（cli-progress） | 有意省略 | 保留 onProgress 回调数据源（out_time/speed 解析已实现） |
| mediainfo 双路径中的 mediainfo 分支 | 平台取舍 | 只实现 ffprobe JSON 路径（跨平台免外部依赖）；`pixelFormat="YUV4:2:0"` 形态的保守位深处理已保留 |
| music-metadata（readMusicMeta） | 简化 | 音频 tags/码率改由 ffprobe format.tags 提供 |
| i18n 双语文案 | 有意省略 | C# 端输出英文固定文案（与 Node 版 log 输出同为英文口径） |

## 四、有差异清单（行为级，逐条说明影响）

1. **GPU 探测方式**：JS 用 `systeminformation`；C# 用 `nvidia-smi`（优先）→ `Get-CimInstance Win32_VideoController`（回退），全部失败降级空列表。影响：vendor/代次解析与矩阵查表逻辑一致，仅枚举来源不同；无显卡/驱动异常时等价于 JS 的「探测失败」路径。
2. **执行模型**：JS 异步（Promise/in-flight 去重/AbortSignal）；C# 同步 + CancellationToken。影响：API 形态不同；probeCache、caps 进程内缓存语义不变。
3. **乱码字符过滤**：`hasBadCJKChar/hasBadUnicode` 未移植。影响：音频 title/artist 等元数据在 C# 端只做引号清洗，乱码值会原样写入 metadata（不影响转码正确性）。
4. **错误日志文件**：只支持文本格式（`key =: value`）；JS 另支持 json。影响：仅排障文件格式。
5. **日志系统**：JS 的彩色终端/文件日志（lib/debug.js）未移植。影响：分层决策、探测跳过等关键信息改为 CLI 输出与异常消息承载。
6. **temp 文件哈希**：`textHash`（xxHash32）改为 8 位十六进制摘要。影响：仅临时文件名中间段不同，唯一性与确定性语义保持。
7. **文件名规则**：`applyFileNameRules` 的 include/exclude/regex/start/count 已实现；JS 端更复杂的 rename 规则体系（lib/rename.js 全量）未纳入。
8. **tree 输出模式**：按「root 前缀字符串替换」近似 JS `pathRewrite`；跨盘符等极端路径与 JS 行为可能不同。

## 五、注意事项（使用与开发）

### 使用

1. **环境**：.NET 10 SDK；ffmpeg/ffprobe 可执行文件。定位顺序 `FFMPEG_PATH → FFMPEG_BINARY → PATH`，ffprobe 追加 ffmpeg 同目录回退。二进制不存在时 `detectHardwareCapabilities` 抛 `hwdetect: ffmpeg not found`。
2. **默认 dry-run**：CLI `run` 不带 `--doit` 只打印计划与完整命令；`plan` 不跑硬件探测（避免无谓探测开销），因此 dry-run 打印的命令用 cpu 层占位，**真实执行的分层以 `run --doit` 为准**。
3. **产物安全**：目标已存在时默认跳过；`--override` 才覆盖（先备份 `.old@时间戳_随机`，提交失败回滚）。
4. **codec 预检**：strict 模式下音频编码器缺失会跳过文件（`strict_codec`），硬件层不可用直接失败不降级——与 JS strict 语义一致，不想硬失败就不要开 strict。

### 开发 / 修改

5. **同构红线**：探测命令（buildProbeArgs）与真实命令（FfmpegBuild）必须走同一条 buildVideoFilters/buildEncoderArgs 路径；改动滤镜/编码器参数时两处天然同步，**不要**在某一侧绕开公共函数单独拼参数。
6. **探测缓存**：只缓存「成功」结果；缓存键必须包含位深、显式编码器、speed、framerate、anime——历史上缺位深/缺 speed 都造成过误判回归，新增影响命令形态的参数时必须进键。
7. **质量偏移**：三层表（QUALITY_OFFSET / VMAF_QUALITY_OFFSET / 按编码器实现分发）各有锚点口径，改任何一层前先读 hwaccel.js 对应注释；不要把所有 codec 族锚到 x264。
8. **编码器参数「精简可靠」原则**：所有 `-c:v` 之后的参数均经真机 N-126733 逐编码器核验，勿凭文档臆造调优项（AMF 路径本机无 A 卡未实拍验证）。
9. **测试**：改动 Planning/Build/Hardware 后跑 `dotnet test`（59 项）；涉及命令形态的改动建议同步跑 parity 对比（方法见 PORTING-NOTES.md 附录）。
10. **NuGet 约束**：类库仅依赖 YamlDotNet；不要引入系统-information 类包或 shell 拼接（进程调用一律 `ProcessStartInfo.ArgumentList`）。
11. **目录命名陷阱**：MSBuild 默认排除 `bin/**`（Windows 大小写不敏感），源码子目录不要命名为 `Bin`。

### 验证记录（本次实施）

- `dotnet build`：3 项目 0 error；`dotnet test`：59/59。
- 真机：`TEST2__hevc_10bit_1080.mp4` → hevc_2k（hevc 1920x1080 10bit + aac）；`TEST2__h264_422_8bit.mkv` → h264_2k（h264 1920x1080 + aac）。
- parity：同一构造输入 JS/C# 命令 390 字符完全一致（`dotnet/docs/PORTING-NOTES.md` 附录）。
