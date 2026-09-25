# PORTING-NOTES — src/transcode → C#/.NET 10 移植说明

记录模块对照、关键语义保持点与已知差异。测试位于 `dotnet/tests/`。

## 1. 模块对照

| Node 版 | C# 版 | 说明 |
|---|---|---|
| preset_schema.js | Presets/PresetSchema.cs | 字段白名单/类型校验，单一事实源 |
| preset_loader.js | Presets/PresetLoader.cs | resolveExtends 循环检测、_override 合并、分层搜索路径 |
| ffmpeg_presets.js | Presets/FFmpegPresets.cs | FFmpegPreset 模型、createFromArgv、ARG_ALIASES/applyFfargs |
| gpu.js | Hardware/Gpu.cs | 代次解析、NVENC/NVDEC 矩阵、nvdecSupportOf 预筛 |
| hwdetect.js | Hardware/HwDetect.cs | 解析器 ×4、设备探测、candidateTiers（T6 链序） |
| hwaccel.js | Hardware/HwAccel.cs | TIERS/ENCODER_MATRIX/质量偏移/滤镜链/buildEncoderArgs/probe/selectTier |
| ffmpeg_plan.js | Planning/FfmpegPlan.cs | calculateDstArgs（幂律缩放/动漫自适应/帧率容差）、命名模板 |
| ffmpeg_build.js | Build/FfmpegBuild.cs | 三段参数组装、字幕模板、MKV 统计标签清理、音频 copy 判定 |
| ffmpeg_run.js | Run/FfmpegRun.cs | resolveHwPlan、进度解析、错误提取、产物提交/回滚 |
| ffmpeg_task.js | Run/FfmpegTask.cs | prepare：跳过判定/严格编码器预检/字幕选择 |
| ffmpeg_scan.js | Scan/FfmpegScan.cs | 收集/类型过滤/规则切片 |
| ffmpeg_result.js | Model/TranscodeEntry.cs (ToRunResult) | 稳定 RunResult 投影 |
| ffmpeg_bin.js | FfmpegBin.cs | FFMPEG_PATH→FFMPEG_BINARY→PATH；ffprobe 含 ffmpeg 同目录回退 |
| lib/mediainfo.js | MediaInfo/FfprobeMediaInfo.cs | 只实现 ffprobe JSON 路径（见差异 §3.4） |
| lib/core.js / lib/helper.js | Support/Core.cs / Support/Helper.cs | 领域内消费的子集 |

未移植（有意省略，属 UI/编排层而非核心）：`ffmpeg_engine.js`、`ffmpeg_events.js`、
`ffmpeg_options.js`（WebUI 选项归一化）、`ffmpeg_plan_snapshot.js`（快照信封）。
CLI 编排（cmd/cmd_ffmpeg.js 的交互确认/汇总/删除源文件）由 `MediaCli.Transcode.Cli`
以最小形式覆盖（presets/plan/run）。

## 2. 关键语义保持点（全部有测试覆盖）

- **尺寸**：长边禁止放大 + 四舍五入偶数（`Math.round` 而非 floor，与 ffmpeg scale 一致）。
- **质量归一化**：三层偏移并存 —— QUALITY_OFFSET（avc_nvenc [5,4,1] 等 3 段阶梯）、
  VMAF_QUALITY_OFFSET（hw-h264 +7 / hw-hevc +5）、按「实际编码器实现」分发
  （swdec 层编码器随厂商变化，按层名查表会错）。
- **编码器参数块**：nvenc（-rc vbr -tune hq -rc-lookahead 30；CQ=-cq + -b:v 0）、
  qsv（-global_quality）、amf（-rc qvbr / vbr_peak）、cpu（-crf/-preset medium；
  av1/vp9 CQ 配 -b:v 0）；anime 参数（-spatial-aq/-temporal-aq、-tune animation、
  -x265-params no-sao=1:aq-mode=3、-svtav1-params tune=0）；CQ + 显式 maxBitrate
  的峰值封顶（amf 只发 -maxrate）。
- **位深对齐**：10bit 源 + h264 目标 → cuda/qsv 走 scale `:format=nv12`（帧不出显存）、
  swdec 走 `-pix_fmt yuv420p`；**位深未知按需要对齐**（保守方向），探测与真实命令
  共用同一 buildVideoFilters/buildEncoderArgs（同构）。
- **探测**：`-frames:v 10` + `-f null -`、只看退出码、缓存键含位深/显式编码器/speed/
  framerate/anime、只缓存成功（损坏文件不污染同键）。
- **候选链**：厂商层 → swdec（有厂商硬件编码器才入场，位置在 d3d 之前）→ d3d → cpu；
  GPU 矩阵预筛只拦「明确 no」；auto+显式 --hwaccel 白名单过滤；normalize("auto")="d3d"
  老陷阱在 auto 分支前拦截。
- **分辨率码率幂律**：scale=(实际像素/锚点像素)^0.75，锚点=源按目标长边等比理论尺寸；
  dstVideoBitrate 仅在指定码率时计算，且被源码率封顶。
- **音频**：smartBitrate 阶梯表、无损兜底 999k、48k 默认、目标不高于源；
  视频文件「码率不升 + 容器兼容」才 copy；speed≠1 强制重编码。
- **元数据**：MKV 统计标签清理（12 参数）、用户 --metadata 后写覆盖、
  comment 写入完整命令行（1000 字符截断）。
- **产物安全**：默认不覆盖、override 先备份后回滚、产物异常小判定（>1MB 源 + ≤20KB 产物）
  不进重试链路、错误提取「第一条 [error] 行/特征词/兜底末行」三级策略。

## 3. 已知差异（有意为之或平台限制）

1. **GPU 探测**：JS 用 `systeminformation`；C# 端无对应包，改用
   `nvidia-smi`（优先）→ `Get-CimInstance Win32_VideoController` 回退，全失败降级空列表。
   vendor 归一化与代次解析逻辑与 JS 一致。
2. **同步执行模型**：JS 的 in-flight promise 去重 / AbortSignal 改为同步 + CancellationToken；
   probeCache / capsCache 语义不变。
3. **音频 tags**：`readMusicMeta`（music-metadata）并入 ffprobe format.tags 路径；
   `hasBadCJKChar/hasBadUnicode` 乱码过滤未移植（C# 端不过滤，仅去引号）——
   标题含乱码时会原样写入 metadata，不影响转码正确性。
4. **mediainfo 双路径**：JS 支持 mediainfo 后备（pixelFormat="YUV4:2:0" + BitDepth 字段）；
   C# 端仅 ffprobe。bitDepthOf/needsDepthAlign 对 "YUV4:2:0" 形态的保守处理已保留，
   因此即便外部传入该形态的 pixelFormat 仍安全。
5. **CLI 交互**：无确认/进度条 UI（dry-run 打印完整命令；run 输出逐文件结果摘要）；
   默认 dry-run、--doit 执行，与 `mediac ffmpeg` 契约一致。
6. **错误文件**：仅文本格式（JS 支持 json 选项），结构简化为 key =: value。
7. **日志**：JS 的彩色终端日志/文件日志未移植；探测与分层决策的关键信息通过
   CLI 输出与异常消息保留。

## 附录：命令等价性验证（parity）

同一段构造输入（hevc_2k 预设 + userArgs(videoCodec=libx265, videoQuality=24) +
hevc 10bit 1920x1080 源信息 + cpu 层 hwPlan），JS 与 C# 生成的完整命令
（390 字符）逐字节一致：

```
-hide_banner -n -v error -progress - -nostats -i <input> -c:s mov_text -map 0:v:0 -map 0:a? -map 0:s?
-c:v libx265 -crf 24 -preset medium -maxrate 8000K -bufsize 8000K -c:a copy
-metadata title=<name> -map_metadata 0 -movflags +faststart -movflags use_metadata_tags <output>
```

（要点：CQ 模式 CRF=24 无偏移、maxBitrate=8M 封顶、音频智能 copy、MP4 字幕 mov_text。）
