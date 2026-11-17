<#
.SYNOPSIS
    HideFiles 发布脚本 — 生成分发产物与 SHA256 校验和

.DESCRIPTION
    1. App zip：dist/HideFilesApp-{ver}-win-x64.zip (包含 HideFilesApp.exe 与 hidefiles.exe)
    2. CLI zip：dist/HideFilesCli-{ver}-win-x64.zip (包含 hidefiles.exe)
    3. 校验和：dist/SHA256SUMS.txt
#>

param(
    [string]$Version = "1.0.0"
)

$ErrorActionPreference = "Stop"
$repoRoot = $PSScriptRoot
$distDir = Join-Path $repoRoot "dist"
$pubTemp = Join-Path $repoRoot "temp" "pub"

Write-Host "========================================" -ForegroundColor Cyan
Write-Host "  HideFiles 发布脚本" -ForegroundColor Cyan
Write-Host "========================================" -ForegroundColor Cyan

# 1. 运行单元测试
Write-Host "[1/5] 执行全量单元测试..." -ForegroundColor Yellow
dotnet test (Join-Path $repoRoot "tests\HideFiles.Tests\HideFiles.Tests.csproj") -c Release --nologo
if ($LASTEXITCODE -ne 0) {
    throw "Unit tests failed!"
}

# 2. 清理目录
Write-Host "[2/5] 准备产物目录..." -ForegroundColor Yellow
if (Test-Path $distDir) { Remove-Item $distDir -Recurse -Force }
if (Test-Path $pubTemp) { Remove-Item $pubTemp -Recurse -Force }
New-Item -ItemType Directory -Path $distDir, $pubTemp -Force | Out-Null

$appPubDir = Join-Path $pubTemp "app"
$cliPubDir = Join-Path $pubTemp "cli"

# 3. 发布 CLI
Write-Host "[3/5] 构建发布 CLI (hidefiles)..." -ForegroundColor Yellow
dotnet publish (Join-Path $repoRoot "src\HideFiles.Cli\HideFiles.Cli.csproj") `
    -c Release -r win-x64 --self-contained false `
    -p:PublishSingleFile=true -o $cliPubDir --nologo

# 4. 发布 App
Write-Host "[4/5] 构建发布 App (HideFilesApp)..." -ForegroundColor Yellow
dotnet publish (Join-Path $repoRoot "src\HideFiles.App\HideFiles.App.csproj") `
    -c Release -r win-x64 --self-contained false `
    -p:PublishSingleFile=true -o $appPubDir --nologo

# 复制 CLI 到 App 目录，便于右键菜单协同工作（CLI 本身也是独立分发产物）
Copy-Item (Join-Path $cliPubDir "hidefiles.exe") (Join-Path $appPubDir "hidefiles.exe") -Force
# 图标资源无需向根目录重复复制：App 的 Assets/Icon/AppIcon.ico 已由 csproj 的
# CopyToOutputDirectory 带入发布目录，避免安装包内出现两份内容相同的 ico。

# 5. 打包 Zip 与生成 SHA256SUMS
Write-Host "[5/5] 打包 Zip 与计算校验和..." -ForegroundColor Yellow
$appZip = Join-Path $distDir "HideFilesApp-$Version-win-x64.zip"
$cliZip = Join-Path $distDir "HideFilesCli-$Version-win-x64.zip"

Compress-Archive -Path "$appPubDir\*" -DestinationPath $appZip -CompressionLevel Optimal
Compress-Archive -Path "$cliPubDir\*" -DestinationPath $cliZip -CompressionLevel Optimal

$sums = @()
$hashApp = (Get-FileHash $appZip -Algorithm SHA256).Hash.ToLowerInvariant()
$hashCli = (Get-FileHash $cliZip -Algorithm SHA256).Hash.ToLowerInvariant()

$sums += "$hashApp  $(Split-Path $appZip -Leaf)"
$sums += "$hashCli  $(Split-Path $cliZip -Leaf)"
$sums | Out-File -FilePath (Join-Path $distDir "SHA256SUMS.txt") -Encoding ascii

Write-Host "发布完成！" -ForegroundColor Green
Get-ChildItem -Path $distDir | Select-Object Name, Length
