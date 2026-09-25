# 端到端冒烟：启动真实 mediac-gui.exe，用 UI Automation 验证控件可见且可用，并截图。
#
# 为什么需要它：进程内测试（xunit）虽然驱动真实 MainForm，但走的是同一进程的
# 已加载程序集。这里额外验证"双击 exe 能正常起来"这一整条链路：
#   程序集解析 → 预设随输出部署 → runtimeconfig 前滚 → 窗口句柄创建 → 控件可被辅助功能读取。
# 后者也是无障碍（读屏软件）能用的前提。
#
# 输出：temp/screenshots/exe-*.png 与控制台断言结果。

[CmdletBinding()]
param(
    # 默认路径相对于本脚本（tests/MediaCli.Transcode.Gui.Tests/），需上溯两级到仓库根
    [string]$ExePath = "$PSScriptRoot\..\..\src\MediaCli.Transcode.Gui\bin\Debug\net8.0-windows\FFConv.exe"
)

$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName UIAutomationClient, UIAutomationTypes, System.Drawing, System.Windows.Forms

if (!(Test-Path $ExePath)) {
    Write-Host "未找到 GUI 可执行文件: $ExePath" -ForegroundColor Red
    Write-Host "请先构建: dotnet build src/MediaCli.Transcode.Gui/MediaCli.Transcode.Gui.csproj"
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

    # 2. 用 UIA 查找关键控件（等价于读屏软件看到的内容）
    Start-Sleep -Seconds 3   # 等启动诊断填充预设下拉

    function FindByName($name) {
        $cond = New-Object System.Windows.Automation.PropertyCondition(
            [System.Windows.Automation.AutomationElement]::NameProperty, $name)
        return $win.FindFirst([System.Windows.Automation.TreeScope]::Descendants, $cond)
    }

    Write-Host "`n--- 控件可达性（UI Automation）---"
    $expected = @(
        '选择文件…', '选择目录…', '预览命令', '开始转码', '取消',
        '清空日志', '打开输出目录', '覆盖已有', '严格模式', '详细日志', '动漫模式'
    )
    foreach ($name in $expected) {
        $el = FindByName $name
        Assert ($null -ne $el) "控件可被辅助功能读取: $name"
    }

    # 3. 控件启用状态（初始：取消应禁用）
    $cancel = FindByName '取消'
    if ($cancel) {
        Assert (-not $cancel.Current.IsEnabled) "初始状态下「取消」为禁用"
    }
    $runBtn = FindByName '开始转码'
    if ($runBtn) {
        Assert ($runBtn.Current.IsEnabled) "初始状态下「开始转码」可用"
    }

    # 4. 截图（PrintWindow，不依赖窗口是否在最前）
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

    Write-Host "`n--- 截图 ---"
    [void][Win32Shot]::SetForegroundWindow($mainHwnd)
    Start-Sleep -Milliseconds 600
    $shot = Capture 'default'
    Assert (Test-Path $shot) "截图已生成"
    Assert ((Get-Item $shot).Length -gt 10000) "截图体积合理（非空白）"

    # 5. 验证程序集与预设随输出部署（真实 exe 能读到 default.yaml 的前提）
    Write-Host "`n--- 部署完整性 ---"
    $outDir = Split-Path $exe -Parent
    Assert (Test-Path (Join-Path $outDir 'presets\default.yaml')) "预设随输出部署"
    Assert (Test-Path (Join-Path $outDir 'FFConv.runtimeconfig.json')) "runtimeconfig 存在"

    $rc = Get-Content (Join-Path $outDir 'FFConv.runtimeconfig.json') -Raw
    Assert ($rc -match '"rollForward"\s*:\s*"Major"') "runtimeconfig 含 rollForward=Major"
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
    Write-Host "端到端冒烟: 全部通过" -ForegroundColor Green
    exit 0
} else {
    Write-Host "端到端冒烟: $($failures.Count) 项失败" -ForegroundColor Red
    $failures | ForEach-Object { Write-Host "  - $_" }
    exit 1
}
