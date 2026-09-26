# FFConv (MediaCli.Transcode)

FFConv 是一个基于 .NET 8 的音视频批量转码与重封装工具，提供 WinForms 桌面客户端（FFConv.exe）与命令行工具（mediac-dotnet）。底层的实际音视频处理委托外部 `ffmpeg` / `ffprobe` 执行，核心库负责硬件加速探测、参数编排与进程管理。

## 项目架构

- **`src/MediaCli.Transcode`**：核心类库。
  - `Hardware`：GPU/CPU 硬件编解码加速探测（NVENC、QSV、AMF、VAAPI、VideoToolbox）。
  - `Presets`：内置与自定义转码预设（格式、编码器族、分辨率、码率、音频等）。
  - `Planning`：转码参数编排（`FfmpegPlan`）与命令行组装（`FfmpegBuild`）。
  - `Run`：进程管理与执行链路（`FfmpegRun`、`FfmpegTask`），支持实时进度解析与平滑取消。
  - `Support`：版本构建信息（`BuildInfo`）与通用工具方法。
- **`src/MediaCli.Transcode.Gui`**：WinForms 桌面端应用。
  - `MainForm`：主界面（文件拖拽、输入/输出路径、预设选择、进度条、实时日志过滤）。
  - `ParamsForm` / `ParamSerializer` / `ParamTrial`：高级参数设置面板、参数往返序列化与单行试算。
  - `AboutForm`：使用说明、CLI 选项表、预设详情与关于信息。
  - `FodyWeavers.xml`：集成 Costura.Fody，生成独立单文件可执行程序。
- **`src/MediaCli.Transcode.Cli`**：CLI 命令行端。
- **`tests/MediaCli.Transcode.Tests`**：核心业务逻辑、预设与参数构建单元测试。
- **`tests/MediaCli.Transcode.Gui.Tests`**：GUI 界面逻辑、参数序列化、解析及布局自动化测试（基于 STA 线程模型）。

## 开发与测试

### 构建

```powershell
# 还原依赖
dotnet restore

# 调试构建
dotnet build

# Release 构建
dotnet build -c Release
```

### 测试

```powershell
# 运行全量单元测试与 GUI 自动化测试
dotnet test
```

### 发布

```powershell
# 执行根目录发布脚本，生成单文件 Release 产物至 publish/
pwsh ./publish.ps1
```

## 开发规范与准则

1. **编码与格式**：所有源码和文档统一使用 **UTF-8** 编码，保持换行符一致。
2. **命名空间与依赖**：
   - 严禁使用 `Build` 作为命名空间或代码目录名（避免与 MSBuild 冲突）；转码构建与编排逻辑统一收敛在 `MediaCli.Transcode.Planning`。
   - 单一事实源：CLI 参数定义以 `CliOptions.All` 为基准。
3. **文件存放**：
   - 文档统一存放于 `docs/`。
   - 临时文件、排查脚本与中间产物必须放在 `temp/`，禁止在项目根目录堆放临时文件。
4. **Git 限制**：
   - 严禁未经许可自发 `git push`；禁止未经用户要求自发 commit。
   - Commit message 采用英文规范（如 `feat(...)`, `fix(...)`, `refactor(...)`）。

## 任务交付验收准则 (Checklist)

每次代码改动或任务完成后，在向用户汇报前**必须**严格执行以下全量验证，缺一不可：

1. **Build 零错误**：运行 `dotnet build -c Release`，确认 0 警告、0 错误。
2. **Test 全量通过**：运行 `dotnet test`，确认所有核心类库与 GUI 测试用例全部通过（0 失败、0 跳过）。
3. **真实流程测试（运行不 Crash）**：
   - 必须执行至少一次**真实的端到端流程测试**（运行 `pwsh tests/MediaCli.Transcode.Gui.Tests/smoke-exe.ps1`）。
   - 验证真实可执行文件启动、参数面板交互、真实音视频输入、预览规划与转码链路、退出码为 0 且全程无任何 Crash。
4. **变动记录追溯**：
   - 重要变动在 `docs/CHANGES-YYYYMMDD.md` 顶部记录真实时间（GMT+8）与准确改动摘要。

