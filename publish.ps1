<#
.SYNOPSIS
    FFConv 发布脚本 — 生成框架依赖 zip 发布产物 + SHA256 校验和

.DESCRIPTION
    发布物包含经过 Costura.Fody 嵌入依赖的发布目录打包 zip（需 .NET 8+ runtime）：

    1. GUI zip：dist/FFConv-{ver}-win-x64.zip           （FFConv.exe + 配置/预设）
    2. CLI zip：dist/mediac-dotnet-{ver}-win-x64.zip   （mediac-dotnet.exe + 预设）
    3. 校验和：dist/SHA256SUMS.txt

.PARAMETER Version
    可选：覆盖版本号标签（默认从 exe 读取，如 1.0.0）

.EXAMPLE
    .\publish.ps1                    # 生成两个 zip + 校验和
    .\publish.ps1 -Version 1.0.0     # 指定版本号
#>

param(
    [string]$Version = ""
)

$ErrorActionPreference = "Stop"
$repoRoot = $PSScriptRoot
$distDir = Join-Path $repoRoot "dist"
$pubTemp = Join-Path $repoRoot "temp" "pub"

Write-Host "========================================" -ForegroundColor Cyan
Write-Host "  FFConv 发布脚本（框架依赖 zip）" -ForegroundColor Cyan
Write-Host "========================================" -ForegroundColor Cyan

# ============================================================
# 1. 清理旧产物
# ============================================================
Write-Host "[1/5] 清理旧产物..." -ForegroundColor Yellow
if (Test-Path $distDir) { Remove-Item $distDir -Recurse -Force }
New-Item -ItemType Directory -Path $distDir -Force | Out-Null
if (Test-Path $pubTemp) { Remove-Item $pubTemp -Recurse -Force }
New-Item -ItemType Directory -Path $pubTemp -Force | Out-Null

$releaseFiles = @()   # 需要计算 SHA256 的发布文件

# ============================================================
# 2. 构建框架依赖版（普通目录）
# ============================================================
Write-Host "[2/5] 构建框架依赖版（需 .NET 8+ runtime）..." -ForegroundColor Yellow
$fdArgs = @(
    "-c", "Release",
    "-r", "win-x64",
    "--self-contained", "false",
    "--nologo",
    "-v", "minimal"
)

$guiFdOut = Join-Path $pubTemp "gui"
& dotnet publish (Join-Path $repoRoot "src/MediaCli.Transcode.Gui") @fdArgs "-o" $guiFdOut
if ($LASTEXITCODE -ne 0) { Write-Host "构建 FFConv GUI 失败" -ForegroundColor Red; exit 1 }

$cliFdOut = Join-Path $pubTemp "cli"
& dotnet publish (Join-Path $repoRoot "src/MediaCli.Transcode.Cli") @fdArgs "-o" $cliFdOut
if ($LASTEXITCODE -ne 0) { Write-Host "构建 mediac-dotnet CLI 失败" -ForegroundColor Red; exit 1 }

# 读取版本号
if ([string]::IsNullOrEmpty($Version)) {
    $ver = (Get-Item (Join-Path $guiFdOut "FFConv.exe")).VersionInfo.ProductVersion
    if ($ver -match '^(\d+\.\d+\.\d+)') { $Version = $Matches[1] }
    else { $Version = "unknown" }
}
Write-Host "  版本: $Version" -ForegroundColor Green

# 删除 pdb（发布产物不需要调试符号）
Get-ChildItem $guiFdOut -Filter "*.pdb" | Remove-Item -Force
Get-ChildItem $cliFdOut -Filter "*.pdb" | Remove-Item -Force

# ============================================================
# 3. 打包 zip（带版本号后缀）
# ============================================================
Write-Host "[3/5] 打包 zip..." -ForegroundColor Yellow
$guiZip = Join-Path $distDir "FFConv-$Version-win-x64.zip"
$cliZip = Join-Path $distDir "mediac-dotnet-$Version-win-x64.zip"
Compress-Archive -Path "$guiFdOut\*" -DestinationPath $guiZip -Force
Compress-Archive -Path "$cliFdOut\*" -DestinationPath $cliZip -Force
$releaseFiles += $guiZip, $cliZip
Write-Host "  FFConv zip: $([math]::Round((Get-Item $guiZip).Length/1MB,2)) MB" -ForegroundColor DarkGray
Write-Host "  mediac-dotnet zip: $([math]::Round((Get-Item $cliZip).Length/1MB,2)) MB" -ForegroundColor DarkGray

# ============================================================
# 4. 生成 SHA256 校验和
# ============================================================
Write-Host "[4/5] 生成 SHA256 校验和..." -ForegroundColor Yellow
$checksumFile = Join-Path $distDir "SHA256SUMS.txt"
$lines = @()

foreach ($file in $releaseFiles) {
    $hash = (Get-FileHash $file -Algorithm SHA256).Hash.ToLower()
    $fileName = Split-Path $file -Leaf
    $lines += "$hash  $fileName"
    $sizeMB = [math]::Round((Get-Item $file).Length / 1MB, 2)
    Write-Host ("  {0} ({1} MB)" -f $fileName, $sizeMB) -ForegroundColor Green
    Write-Host ("    SHA256: {0}" -f $hash) -ForegroundColor DarkGray
}

# 写入校验和文件（每行 "hash  filename"，末尾换行）
$content = ($lines -join "`n") + "`n"
[System.IO.File]::WriteAllText($checksumFile, $content, (New-Object System.Text.UTF8Encoding $false))

# 清理临时目录
Remove-Item $pubTemp -Recurse -Force -ErrorAction SilentlyContinue

# ============================================================
# 5. 输出发布清单
# ============================================================
Write-Host ""
Write-Host "[5/5] 发布完成！" -ForegroundColor Green
Write-Host "========================================" -ForegroundColor Green
Write-Host "输出目录: $distDir" -ForegroundColor Cyan
Write-Host ""
Write-Host "可下载文件:" -ForegroundColor Cyan
Get-ChildItem $distDir -File | ForEach-Object {
    $sizeMB = [math]::Round($_.Length / 1MB, 2)
    Write-Host ("  {0}  ({1} MB)" -f $_.Name, $sizeMB) -ForegroundColor White
}
