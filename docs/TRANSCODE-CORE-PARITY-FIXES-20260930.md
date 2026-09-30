# FFConvNet 与 ffconv-electron 业务逻辑核心非GUI部分对齐分析与修复计划

- 日期：2026-09-30 (GMT+8)
- 比对基准：C:\Home\Projects\ffconv-electron (提交 fe52447 ~ 50a76e4)
- 目标工程：c:\Home\Projects\FFConvNet (src/MediaCli.Transcode)

---

## 1. 背景与审计结论

近期 ffconv-electron 仓库对转码管道（core/transcode、core/lib）进行了多轮高强度的代码审查与真实转码实测，修复了大量由于容器特性（MKV/WebM/MP4）、流映射机制、预设分层继承机制、10-bit 高位深滤镜链处理、硬件加速探测穿透等引起的严重 Bug。

经与当前 C# 核心库 MediaCli.Transcode 逐行比对核查，发现 C# 端依然存在 14 项未同步的业务逻辑缺陷。按影响严重程度分为 P0（数据损坏/假成功）、P1（功能失效/必崩/预设丢弃）与 P2（参数与性能瑕疵）。

---

## 2. 缺陷清单与修复计划

### 【P0 级】数据正确性与假成功

#### 1. 内嵌封面图被误选为主视频流，导致输出 1 帧静态图却误报“转码成功”
- **根因**：
  1. FfprobeMediaInfo.cs 取第一个 codec_type == "video" 的流，未排除内嵌封面图（Cover art）。MKV 容器中封面通常为 stream 0（mjpeg/png 等静帧编码器，且无 attached_pic 标记），真实视频为 stream 1；
  2. VideoInfo 缺少绝对流序号 StreamIndex；
  3. FfmpegBuild.cs 硬编码了 -map 0:v:0（“第 0 个 video 类型流”），精准选中了封面图，导致转码只输出了 1 帧静态图片；ffmpeg 退出码为 0，程序误报成功。
- **修复措施**：
  1. Model/MediaInfo.cs 的 VideoInfo 增加 public int? StreamIndex { get; set; }；
  2. FfprobeMediaInfo.cs 实现封面识别（三档判定：正常视频、疑似封面、确证封面），计算并填充 StreamIndex；若是纯音频文件内嵌封面，则不标记视频流；
  3. FfmpegBuild.cs 流映射改用 -map 0:<StreamIndex>；若绝对流序号缺失，则整组 -map 均不输出，交由 ffmpeg 默认流选择机制处理（默认行为会自动跳过封面）。

---

### 【P1 级】核心功能失效 / 必崩命令 / 预设丢弃

#### 2. audio_extract 预设作用于视频文件时生成重复双音轨
- **根因**：FfmpegBuild.cs 中 AppendSubtitleArgs 的守卫写成了 if (tempPreset.Type != "video" && !Helper.IsVideoFile(entry.Path)) return;。当音频预设（Type == "audio"）作用于视频文件时未提前退出，追加了 -map 0:a?，与预设原生的 -map 0:a:0 叠加，产出两条一模一样的音频流。
- **修复措施**：守卫条件改为只看预设类型：if (tempPreset.Type != "video") return;。

#### 3. 10-bit 源在用户滤镜下因硬件下载域写死 format=nv12 导致转码直接报错崩溃
- **根因**：HwAccel.cs 中显存帧跑软件滤镜（如 yadif）时硬编码 chain.AddRange(["hwdownload", "format=nv12"]);。但 10-bit 硬件帧无法下载为 nv12，导致报错 Invalid output format nv12 for hwframe download 并崩溃。
- **修复措施**：仅当目标编码器确实必须 8-bit（h264 族且需位深对齐）时使用 nv12，否则按源高位深使用 format=p010le。

#### 4. WebM 目标容器未拦截字幕，导致注入 mov_text 引发 muxer 硬崩溃
- **根因**：FfmpegBuild.cs 仅判断了 isMkv，目标为 WebM 时落入 MP4 分支，注入了 -c:s mov_text。但 WebM 仅支持 WebVTT 字幕，被 muxer 直接拒绝报错。
- **修复措施**：若目标格式包含 webm，检测到字幕时统一降级为 -sn 丢弃字幕轨道并告警。

#### 5. 音频流复制（--audio-copy）缺乏容器兼容性检查导致硬报错
- **根因**：FfmpegBuild.cs 在用户指定 --audio-copy 或预设 audioCodec == "copy" 时无条件放行，未做 IsAudioCodecCompatibleWithContainer 检查。若源为 MKV 的 Vorbis/FLAC/Opus，目标为 MP4 时直接硬报错。
- **修复措施**：显式 copy 必须通过容器兼容性校验；不兼容时发出警告并降级为重编码 aac（且修复重编码时编码器名为 "copy" 的问题）。

#### 6. 用户层预设无法跨层 extends 内置预设，报 KeyNotFoundException
- **根因**：FFmpegPresets.Init 对每个层独立调用 ProcessPresets，PresetLoader.ResolveExtends 仅在当前层查找基类。用户在 ~/.mediac/presets.yaml 中继承内置预设时找不到基类直接被丢弃。
- **修复措施**：ResolveExtends 与 ProcessPresets 增加 basePresets 备选查找域，LoadPresetLayers 逐层累积已解析的基础预设。

#### 7. --hwaccel cpu 被静默穿透为 auto 默认 GPU 候选链
- **根因**：HwDetect.cs 第 329 行 if (name is not null && name != "cpu")，当指定 cpu 时直接跳过白名单过滤，穿透到底部的厂商 GPU 候选链，导致 --hwaccel cpu 实际依然启用了 GPU 加速。
- **修复措施**：在 CandidateTiers 头部，当 NormalizeHwaccelName(hwaccel) == "cpu" 时直接返回 ["cpu"]。

#### 8. 已成功落盘的文件在收到取消信号时被篡改为“已取消”
- **根因**：FfmpegRun.cs 在执行完成后，若 signal.IsCancellationRequested 为真，不分情况直接覆盖 entry.Cancelled = true，导致已完成生成的文件被误判为未完成。
- **修复措施**：若产物文件已成功校验并落盘，优先保留 entry.Ok = true 正常完成状态。

#### 9. 预设未知字段与类型错误仅告警未真正剔除
- **根因**：PresetLoader.cs 的 ValidatePresetFields 仅检测了 unknown 和 mismatch 字段，并未将其从 preset 字典中删除，导致错误类型字段（如字符串 dimension）进入后续数值计算导致每个文件跳过。
- **修复措施**：从 preset 字典中真正移除未知和类型不匹配字段，回退到继承值或默认值。

---

### 【P2 级】参数解析、性能与健壮性

#### 10. 内置预设自带 {scaleFilter} 导致 1:1 分辨率下多执行一次无意义重采样
- **根因**：FfmpegBuild.cs 将 scaleRequested 恒为 true 参与 hasScale 计算，导致 1080p 在 1080p 预设下被多插入了一道 scale=w=1920:h=1080。
- **修复措施**：仅当计算出的输出尺寸与原尺寸不一致（考虑偶数对齐），或明确标记了 Scaled 时才启用缩放滤镜。

#### 11. ffargs 带单位码率（vb=233k）被静默丢弃，且 an/anime 别名缺失消费分支
- **根因**：FFmpegPresets.ApplyFfargs 排除字符串码率，且 switch 缺失 case "anime"。
- **修复措施**：支持字符串码率解析，补充 case "anime" 分支。

#### 12. 硬件探测缓存键缺少 quality
- **根因**：HwAccel.ProbeCacheKey 未包含 quality，长驻进程中切换 CRF/CQ 时可能复用旧探测结果。
- **修复措施**：缓存键纳入 quality 参数。

#### 13. --video-copy 或 -c:v copy 未收口清洗预设字段
- **根因**：CreateFromArgv 判定 copy 时未将 dimension/framerate/speed 清空，导致 DstArgs 标记失真。
- **修复措施**：copy 模式下收口重置为 Dimension=0, Framerate=0, Speed=1。

#### 14. 静态硬件能力探测缺乏超时保护
- **根因**：HwDetect.cs 串行执行 4 条命令，使用默认 30s 超时，ffmpeg 异常时最长挂起 120s。
- **修复措施**：将单次探测超时限定为 8000ms，异常时安全降级。

---

## 3. 实施与验证步骤

1. 第 1 阶段：修改 Model/MediaInfo.cs 与 FfprobeMediaInfo.cs（封面过滤与绝对流序号支持）；
2. 第 2 阶段：修改 FfmpegBuild.cs（流映射绝对序号、重复音轨修复、WebM 字幕过滤、音频复制容器校验、1:1 重采样消除）；
3. 第 3 阶段：修改 HwAccel.cs 与 HwDetect.cs（10-bit 软件滤镜域对齐、--hwaccel cpu 生效、探测缓存键 quality 纳入、探测 8s 超时）；
4. 第 4 阶段：修改 PresetLoader.cs 与 FFmpegPresets.cs（跨层 extends 支持、字段坏类型剔除、ffargs 单位支持、copy 收口）；
5. 第 5 阶段：修改 FfmpegRun.cs（已落盘产物取消保护）；
6. 第 6 阶段：运行全量单元测试（dotnet test）与冒烟测试（smoke-exe.ps1），记录 CHANGES-20260930.md。