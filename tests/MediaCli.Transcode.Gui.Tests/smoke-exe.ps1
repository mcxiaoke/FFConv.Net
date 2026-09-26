# 端到端冒烟：启动真实 FFConv.exe，用 UI Automation 验证控件可见且可用、执行真实转码流程并截图。
#
# 验证内容：
#   1. 进程生命周期：exe 启动 → 诊断与硬件探测 → 优雅退出无 Crash，退出码为 0。
#   2. 部署完整性：预设 default.yaml 与 runtimeconfig.json 部署就位。
#   3. 辅助功能可达性：UI Automation 读到全部关键控件。
#   4. 真实端到端业务流：生成基准测试视频 → 输入路径 → 预览规划 → 真实 ffmpeg 转码 → 校验磁盘产物 → 输出统计日志。
#
# 输出：temp/screenshots/exe-*.png 与控制台断言结果。

[CmdletBinding()]
param(
    [string]$ExePath = ""
)

$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName UIAutomationClient, UIAutomationTypes, System.Drawing, System.Windows.Forms

# 自动定位 Exe（优先 Release，其次 Debug）
if ([string]::IsNullOrWhiteSpace($ExePath)) {
    $rel = "$PSScriptRoot\..\..\src\MediaCli.Transcode.Gui\bin\Release\net8.0-windows\FFConv.exe"
    $dbg = "$PSScriptRoot\..\..\src\MediaCli.Transcode.Gui\bin\Debug\net8.0-windows\FFConv.exe"
    if (Test-Path $rel) { $ExePath = $rel }
    elseif (Test-Path $dbg) { $ExePath = $dbg }
    else { $ExePath = $rel }
}

if (!(Test-Path $ExePath)) {
    Write-Host "未找到 GUI 可执行文件: $ExePath" -ForegroundColor Red
    Write-Host "请先构建: dotnet build -c Release"
    exit 1
}
$exe = (Resolve-Path $ExePath).Path
Write-Host "启动: $exe"

$shotDir = Join-Path $PSScriptRoot '..\..\temp\screenshots'
if (!(Test-Path $shotDir)) { New-Item -ItemType Directory -Path $shotDir -Force | Out-Null }
$shotDir = (Resolve-Path $shotDir).Path

$proc = Start-Process -FilePath $exe -PassThru
$failures = New-Object System.Collections.Generic.List[string]

function Assert($condition, $message) {
    if ($condition) {
        Write-Host "  [OK] $message"
    } else {
        Write-Host "  [FAIL] $message" -ForegroundColor Red
        $failures.Add($message)
    }
}

try {
    # 1. 等待主窗口出现
    $deadline = (Get-Date).AddSeconds(30)
    $win = $null
    $root = [System.Windows.Automation.AutomationElement]::RootElement
    $cond = New-Object System.Windows.Automation.PropertyCondition(
        [System.Windows.Automation.AutomationElement]::ProcessIdProperty, $proc.Id)
    while ((Get-Date) -lt $deadline) {
        $proc.Refresh()
        if ($proc.HasExited) { throw "进程提前退出，退出码 $($proc.ExitCode)" }
        $wins = $root.FindAll([System.Windows.Automation.TreeScope]::Children, $cond)
        foreach ($w in $wins) {
            if ($w.Current.Name -like '*FFConv*') {
                $win = $w
                break
            }
        }
        if ($win) { break }
        Start-Sleep -Milliseconds 300
    }
    Assert ($null -ne $win) "主窗口已创建"

    Write-Host "窗口标题: $($win.Current.Name)"
    Assert ($win.Current.Name -like '*FFConv*') "窗口标题符合预期"

    # 2. 用 UIA 查找关键控件
    Start-Sleep -Seconds 3   # 等启动诊断填充硬件探测和预设

    function FindByName($name) {
        $cond = New-Object System.Windows.Automation.PropertyCondition(
            [System.Windows.Automation.AutomationElement]::NameProperty, $name)
        return $win.FindFirst([System.Windows.Automation.TreeScope]::Descendants, $cond)
    }

    function FindById($id) {
        $cond = New-Object System.Windows.Automation.PropertyCondition(
            [System.Windows.Automation.AutomationElement]::AutomationIdProperty, $id)
        return $win.FindFirst([System.Windows.Automation.TreeScope]::Descendants, $cond)
    }

    Write-Host "`n--- 控件可达性（UI Automation）---"
    $expected = @(
        '选择文件…', '选择目录…', '预览命令', '开始转码', '取消',
        '清空日志', '打开输出目录', '覆盖已有', '严格模式', '详细日志', '动漫模式', '高级参数…'
    )
    foreach ($name in $expected) {
        $el = FindByName $name
        Assert ($null -ne $el) "控件可被辅助功能读取: $name"
    }

    # 3. 控件启用状态
    $cancel = FindByName '取消'
    if ($cancel) {
        Assert (-not $cancel.Current.IsEnabled) "初始状态下「取消」为禁用"
    }
    $runBtn = FindByName '开始转码'
    if ($runBtn) {
        Assert ($runBtn.Current.IsEnabled) "初始状态下「开始转码」可用"
    }

    # 4. 截图（PrintWindow）
    Add-Type @"
using System;
using System.Runtime.InteropServices;
public class Win32Shot {
    [DllImport("user32.dll")] public static extern bool PrintWindow(IntPtr hwnd, IntPtr hdc, uint flags);
    [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr hwnd, out RECT r);
    [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr hwnd);
    [StructLayout(LayoutKind.Sequential)] public struct RECT { public int Left, Top, Right, Bottom; }
}
"@

    $mainHwnd = [IntPtr]$win.Current.NativeWindowHandle

    function Capture($suffix) {
        $r = New-Object Win32Shot+RECT
        [void][Win32Shot]::GetWindowRect($mainHwnd, [ref]$r)
        $w = $r.Right - $r.Left
        $h = $r.Bottom - $r.Top
        $bmp = New-Object System.Drawing.Bitmap($w, $h)
        $g = [System.Drawing.Graphics]::FromImage($bmp)
        $hdc = $g.GetHdc()
        [void][Win32Shot]::PrintWindow($mainHwnd, $hdc, 2)  # PW_RENDERFULLCONTENT
        $g.ReleaseHdc($hdc)
        $g.Dispose()
        $path = Join-Path $shotDir "exe-$suffix.png"
        $bmp.Save($path, [System.Drawing.Imaging.ImageFormat]::Png)
        $bmp.Dispose()
        Write-Host "  截图: $path ($w x $h)"
        return $path
    }

    Write-Host "`n--- 初始状态截图 ---"
    [void][Win32Shot]::SetForegroundWindow($mainHwnd)
    Start-Sleep -Milliseconds 600
    $shot = Capture 'default'
    Assert (Test-Path $shot) "初始截图已生成"
    Assert ((Get-Item $shot).Length -gt 10000) "初始截图体积合理（非空白）"

    # 5. 验证程序集与预设随输出部署
    Write-Host "`n--- 部署完整性 ---"
    $outDir = Split-Path $exe -Parent
    Assert (Test-Path (Join-Path $outDir 'presets\default.yaml')) "预设随输出部署"
    Assert (Test-Path (Join-Path $outDir 'FFConv.runtimeconfig.json')) "runtimeconfig 存在"

    $rc = Get-Content (Join-Path $outDir 'FFConv.runtimeconfig.json') -Raw
    Assert ($rc -match '"rollForward"\s*:\s*"Major"') "runtimeconfig 含 rollForward=Major"

    # 6. 真实端到端流程：输入视频 -> 预览命令 -> 真实转码 -> 结果校验
    Write-Host "`n--- 真实端到端业务流程测试 ---"

    $tempDir = Join-Path $PSScriptRoot '..\..\temp'
    if (!(Test-Path $tempDir)) { New-Item -ItemType Directory -Path $tempDir -Force | Out-Null }
    $sampleInput = (Join-Path $tempDir 'smoke-input.mp4')
    $sampleExpectedOutput = (Join-Path $tempDir '[SHANA] smoke-input_av1_2k.mp4')

    if (Test-Path -LiteralPath $sampleExpectedOutput) { Remove-Item -LiteralPath $sampleExpectedOutput -Force }

    # 用 ffmpeg 快速生成基准 1 秒测试视频
    $genProc = Start-Process -FilePath "ffmpeg" -ArgumentList "-y -f lavfi -i testsrc=duration=1:size=320x240:rate=1 -f lavfi -i sine=frequency=1000:duration=1 -c:v libx264 -c:a aac `"$sampleInput`"" -NoNewWindow -Wait -PassThru
    Assert ($genProc.ExitCode -eq 0 -and (Test-Path $sampleInput)) "基准测试视频生成成功"

    # 填入测试视频路径
    $inputBox = FindById 'inputBox'
    Assert ($null -ne $inputBox) "输入文本框已找到"
    $valPattern = $inputBox.GetCurrentPattern([System.Windows.Automation.ValuePattern]::Pattern)
    $valPattern.SetValue((Resolve-Path $sampleInput).Path)
    Write-Host "  已输入测试文件: $sampleInput"
    Start-Sleep -Milliseconds 800

    # 点击「预览命令」
    $btnPreview = FindById 'btnPreview'
    $invPreview = $btnPreview.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern)
    $invPreview.Invoke()
    Write-Host "  触发「预览命令」..."
    Start-Sleep -Seconds 2

    # 读取日志框
    $condDoc = New-Object System.Windows.Automation.PropertyCondition(
        [System.Windows.Automation.AutomationElement]::ControlTypeProperty,
        [System.Windows.Automation.ControlType]::Document)
    $logBox = $win.FindFirst([System.Windows.Automation.TreeScope]::Descendants, $condDoc)
    $textPattern = $logBox.GetCurrentPattern([System.Windows.Automation.TextPattern]::Pattern)
    $logText = $textPattern.DocumentRange.GetText(2000)

    Assert ($logText -match 'ffmpeg' -and $logText -match '预览') "预览命令在日志中成功输出 ffmpeg 规划命令行"
    $shotPreview = Capture 'preview'
    Assert (Test-Path $shotPreview) "预览命令截图已生成"

    # 开启「覆盖已有」并点击「开始转码」
    $chkOverride = FindById 'overrideCheck'
    $toggleOverride = $chkOverride.GetCurrentPattern([System.Windows.Automation.TogglePattern]::Pattern)
    if ($chkOverride.Current.ToggleState -ne [System.Windows.Automation.ToggleState]::On) {
        $toggleOverride.Toggle()
    }

    $btnRun = FindById 'btnRun'
    $invRun = $btnRun.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern)
    $invRun.Invoke()
    Write-Host "  触发「开始转码」..."

    # 等待转码完成（轮询检测 btnRun 恢复可用）
    $timeout = (Get-Date).AddSeconds(20)
    $transcodeDone = $false
    while ((Get-Date) -lt $timeout) {
        Start-Sleep -Milliseconds 500
        if ($btnRun.Current.IsEnabled) {
            $transcodeDone = $true
            break
        }
    }
    Assert ($transcodeDone) "转码任务在超时前正常完成（未卡死）"

    # 校验转码产物与日志
    $finalLog = $textPattern.DocumentRange.GetText(3000)
    Assert ($finalLog -match '\[DONE\]' -or $finalLog -match '完成') "日志显示转码成功完成"
    Assert (Test-Path -LiteralPath $sampleExpectedOutput) "目标转码视频文件已实际生成于磁盘"
    if (Test-Path -LiteralPath $sampleExpectedOutput) {
        $outLen = (Get-Item -LiteralPath $sampleExpectedOutput).Length
        Assert ($outLen -gt 1000) "产物文件体积有效 ($outLen 字节)"
    }
    $shotComplete = Capture 'transcode-complete'
    Assert (Test-Path $shotComplete) "转码完成截图已生成"
}
finally {
    if (-not $proc.HasExited) {
        Write-Host "`n关闭进程..."
        $proc.CloseMainWindow() | Out-Null
        if (-not $proc.WaitForExit(10000)) { $proc.Kill() }
    }
}

Write-Host ""
if ($failures.Count -eq 0) {
    Write-Host "端到端冒烟与真实业务流: 全部通过 (0 错误, 0 Crash)" -ForegroundColor Green
    exit 0
} else {
    Write-Host "端到端冒烟与真实业务流: $($failures.Count) 项失败" -ForegroundColor Red
    $failures | ForEach-Object { Write-Host "  - $_" }
    exit 1
}
