#Requires -Version 5.1
<#
.SYNOPSIS
  Build the saferm binary from src/ into build/.

.DESCRIPTION
  Compiles the Go module in src/ (entry point at cmd/saferm) and writes the
  binaries into build/. Artifact naming follows the sibling `filelist` project:
    build/saferm[.exe]              current platform
    build/saferm-windows-amd64.exe
    build/saferm-windows-arm64.exe
    build/saferm-linux-amd64
    build/saferm-linux-arm64

  Cross-compilation always runs with CGO_ENABLED=0, which is what guarantees
  the "single file, zero runtime dependencies" property in the design doc.

.PARAMETER Target
  Build target: "auto" (current OS/arch), "windows", "linux", or "all"
  (all four GOOS/GOARCH combinations). Default: auto

.PARAMETER Version
  Version string injected into the binary via -ldflags. Default: 1.0.0-dev

.PARAMETER Clean
  Remove build/ before building.

.PARAMETER NoSmoke
  Skip running the freshly built binary's `version` subcommand.

.EXAMPLE
  ./build.ps1
  Build for the current platform and smoke-test the result.

.EXAMPLE
  ./build.ps1 -Target all
  Cross-compile windows/linux x amd64/arm64.

.EXAMPLE
  ./build.ps1 -Target linux -Version 1.0.0 -Clean
  Clean build/ then build the Linux binaries at version 1.0.0.
#>

[CmdletBinding()]
param(
    [ValidateSet("auto", "windows", "linux", "all")]
    [string]$Target = "auto",

    [string]$Version = "1.0.0-dev",

    [switch]$Clean,

    [switch]$NoSmoke
)

$ErrorActionPreference = "Stop"

# --- Paths ---
$ScriptDir = $PSScriptRoot
$SrcDir    = Join-Path $ScriptDir "src"
$BuildDir  = Join-Path $ScriptDir "build"
$MainPkg   = "./cmd/saferm"

# --- Pre-checks ---
$goCmd = Get-Command go -ErrorAction SilentlyContinue
if (-not $goCmd) {
    Write-Error "Go is not installed or not in PATH. Install from https://go.dev/dl/"
    exit 1
}
if (-not (Test-Path (Join-Path $SrcDir "go.mod"))) {
    Write-Error "go.mod not found in $SrcDir"
    exit 1
}
if (-not (Test-Path (Join-Path $SrcDir "cmd/saferm"))) {
    Write-Error "entry point cmd/saferm not found in $SrcDir"
    exit 1
}

# --- Version info ---
$GitCommit = ""
try {
    $commit = git -C $ScriptDir rev-parse --short HEAD 2>$null
    if ($LASTEXITCODE -eq 0 -and $commit) {
        $GitCommit = $commit.Trim()
        $dirty = git -C $ScriptDir status --porcelain 2>$null
        if ($dirty) { $GitCommit += "-dirty" }
    }
} catch {
    # git not available - not fatal
}
$BuildTime = Get-Date -Format "yyyy-MM-dd HH:mm:ss"

# 只注入 main.version：main.go 里就定义了这一个变量。
# 不做 -X main.gitCommit 之类的注入，因为那需要在源码里新增变量并改变 -V 的输出。
#
# -trimpath + -buildvcs=false：与 Makefile 保持一致。本工具没有把 VCS 信息打进
# 二进制（-V 只用 version），关掉 buildvcs 可以避免在 .git 不可读的环境里构建失败。
$LdFlags = "-s -w -X main.version=$Version"

Write-Host "saferm build" -ForegroundColor Cyan
Write-Host "  Source:  $SrcDir"
Write-Host "  Output:  $BuildDir"
Write-Host "  Version: $Version"
if ($GitCommit) { Write-Host "  Commit:  $GitCommit" }
Write-Host "  Time:    $BuildTime"
Write-Host "  Target:  $Target"
Write-Host ""

# --- Clean ---
if ($Clean -and (Test-Path $BuildDir)) {
    Remove-Item $BuildDir -Recurse -Force
    Write-Host "Cleaned build/" -ForegroundColor Yellow
}

if (-not (Test-Path $BuildDir)) {
    New-Item -ItemType Directory -Path $BuildDir -Force | Out-Null
}

# --- Current platform detection (PS 5.1 compatible) ---
$currentOS = $env:GOOS
if (-not $currentOS) {
    if ($PSVersionTable.PSVersion.Major -ge 6) {
        if ($IsWindows) { $currentOS = "windows" } else { $currentOS = "linux" }
    } elseif ($env:OS -eq "Windows_NT") {
        $currentOS = "windows"
    } else {
        $currentOS = "linux"
    }
}

$currentArch = $env:GOARCH
if (-not $currentArch) {
    # 本脚本只在 amd64 主机上用于选择"auto"目标的文件名，不做架构仿真
    $currentArch = "amd64"
}

# --- Build ---
function Invoke-Build {
    param(
        [string]$GOOS,
        [string]$GOARCH,
        [string]$OutputName
    )

    $env:GOOS        = $GOOS
    $env:GOARCH      = $GOARCH
    $env:CGO_ENABLED = 0

    $outputPath = Join-Path $BuildDir $OutputName

    Write-Host "Building $GOOS/$GOARCH -> $OutputName" -ForegroundColor Green
    Push-Location $SrcDir
    try {
        & go build -trimpath -buildvcs=false -ldflags "$LdFlags" -o $outputPath $MainPkg
        if ($LASTEXITCODE -ne 0) {
            throw "go build failed for $GOOS/$GOARCH"
        }
    }
    finally {
        Pop-Location
    }

    $size = (Get-Item $outputPath).Length / 1MB
    Write-Host ("  OK: {0} ({1:N1} MB)" -f $OutputName, $size) -ForegroundColor Green
}

$ext = if ($currentOS -eq "windows") { ".exe" } else { "" }
$targets = @()
switch ($Target) {
    "auto" {
        $targets += @{ GOOS = $currentOS; GOARCH = $currentArch; Name = "saferm$ext" }
    }
    "windows" {
        $targets += @{ GOOS = "windows"; GOARCH = "amd64"; Name = "saferm-windows-amd64.exe" }
        $targets += @{ GOOS = "windows"; GOARCH = "arm64"; Name = "saferm-windows-arm64.exe" }
    }
    "linux" {
        $targets += @{ GOOS = "linux"; GOARCH = "amd64"; Name = "saferm-linux-amd64" }
        $targets += @{ GOOS = "linux"; GOARCH = "arm64"; Name = "saferm-linux-arm64" }
    }
    "all" {
        $targets += @{ GOOS = "windows"; GOARCH = "amd64"; Name = "saferm-windows-amd64.exe" }
        $targets += @{ GOOS = "windows"; GOARCH = "arm64"; Name = "saferm-windows-arm64.exe" }
        $targets += @{ GOOS = "linux";   GOARCH = "amd64"; Name = "saferm-linux-amd64" }
        $targets += @{ GOOS = "linux";   GOARCH = "arm64"; Name = "saferm-linux-arm64" }
    }
}

foreach ($t in $targets) {
    Invoke-Build -GOOS $t.GOOS -GOARCH $t.GOARCH -OutputName $t.Name
}

# --- Reset env so the caller's shell is not left with cross-compile settings ---
$env:GOOS        = $null
$env:GOARCH      = $null
$env:CGO_ENABLED = $null

# --- Smoke test: only possible when the target matches the host ---
if (-not $NoSmoke) {
    foreach ($t in $targets) {
        if ($t.GOOS -eq $currentOS -and $t.GOARCH -eq $currentArch) {
            $probe = Join-Path $BuildDir $t.Name
            Write-Host ""
            Write-Host "Smoke test: $probe version" -ForegroundColor Cyan
            & $probe version
            if ($LASTEXITCODE -ne 0) {
                throw "smoke test failed: $probe version"
            }
            break
        }
    }
}

# --- Copy the sample config next to the binaries ---
$sampleSrc = Join-Path $ScriptDir "config.sample.toml"
if (Test-Path $sampleSrc) {
    Copy-Item $sampleSrc (Join-Path $BuildDir "config.sample.toml") -Force
    Write-Host "Copied config.sample.toml to build/" -ForegroundColor DarkGray
}

Write-Host ""
Write-Host "Done. Build artifacts in build/:" -ForegroundColor Cyan
Get-ChildItem $BuildDir -File | ForEach-Object {
    Write-Host ("  {0,-34} {1:N1} MB" -f $_.Name, ($_.Length / 1MB))
}
