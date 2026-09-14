#Requires -Version 5.1
<#
.SYNOPSIS
  Build FileList binary from src/ into build/.

.DESCRIPTION
  Compiles the Go project located in src/ and outputs binaries to build/.
  Supports current-platform, Windows, Linux, and all targets.

.PARAMETER Target
  Build target: "auto" (current OS), "windows", "linux", or "all".
  Default: auto

.PARAMETER Clean
  Remove build/ before building.

.EXAMPLE
  ./build.ps1
  Build for current platform.

.EXAMPLE
  ./build.ps1 -Target all
  Cross-compile for both Windows and Linux.

.EXAMPLE
  ./build.ps1 -Target linux -Clean
  Clean build/ then build for Linux only.
#>

[CmdletBinding()]
param(
    [ValidateSet("auto", "windows", "linux", "all")]
    [string]$Target = "auto",

    [switch]$Clean
)

$ErrorActionPreference = "Stop"

# --- Paths ---
$ScriptDir = $PSScriptRoot
$SrcDir    = Join-Path $ScriptDir "src"
$BuildDir  = Join-Path $ScriptDir "build"

# --- Pre-checks ---
$goCmd = (Get-Command go -ErrorAction SilentlyContinue)
if (-not $goCmd) {
    Write-Error "Go is not installed or not in PATH. Install from https://go.dev/dl/"
    exit 1
}

if (-not (Test-Path (Join-Path $SrcDir "go.mod"))) {
    Write-Error "go.mod not found in $SrcDir"
    exit 1
}

# --- Version and build info from git ---
$Version = "0.2.0"
$GitCommit = ""
$BuildTime = (Get-Date -Format "yyyy-MM-dd HH:mm:ss")
try {
    $commit = git -C $ScriptDir rev-parse --short HEAD 2>$null
    if ($LASTEXITCODE -eq 0 -and $commit) {
        $GitCommit = $commit.Trim()
        $dirty = git -C $ScriptDir status --porcelain 2>$null
        if ($dirty) { $GitCommit += "-dirty" }
    }
} catch {
    # git not available
}

$LdFlags = "-s -w -X main.gitCommit=$GitCommit -X `"main.buildTime=$BuildTime`""
Write-Host "FileList build" -ForegroundColor Cyan
Write-Host "  Source:  $SrcDir"
Write-Host "  Output:  $BuildDir"
Write-Host "  Version: $Version"
Write-Host "  Commit:  $GitCommit"
Write-Host "  Time:    $BuildTime"
Write-Host "  Target:  $Target"
Write-Host ""

# --- Clean ---
if ($Clean -and (Test-Path $BuildDir)) {
    Remove-Item $BuildDir -Recurse -Force
    Write-Host "Cleaned build/" -ForegroundColor Yellow
}

# --- Ensure build dir ---
if (-not (Test-Path $BuildDir)) {
    New-Item -ItemType Directory -Path $BuildDir -Force | Out-Null
}

# --- Build function ---
function Invoke-Build {
    param(
        [string]$GOOS,
        [string]$GOARCH,
        [string]$OutputName
    )

    $env:GOOS   = $GOOS
    $env:GOARCH = $GOARCH
    $env:CGO_ENABLED = 0

    $outputPath = Join-Path $BuildDir $OutputName

    Write-Host "Building $GOOS/$GOARCH -> $OutputName" -ForegroundColor Green
    Push-Location $SrcDir
    try {
        & go build -ldflags $LdFlags -o $outputPath .
        if ($LASTEXITCODE -ne 0) {
            Write-Error "Build failed for $GOOS/$GOARCH"
            exit 1
        }
    }
    finally {
        Pop-Location
    }

    $size = (Get-Item $outputPath).Length / 1MB
    Write-Host ("  OK: {0} ({1:N1} MB)" -f $OutputName, $size) -ForegroundColor Green
}

# --- Determine targets ---
$currentOS = $env:GOOS
if (-not $currentOS) {
    # infer from runtime
    if ($PSVersionTable.Platform -eq "Unix") {
        $currentOS = "linux"
    } else {
        $currentOS = "windows"
    }
}

$targets = @()
switch ($Target) {
    "auto" {
        $ext = if ($currentOS -eq "windows") { ".exe" } else { "" }
        $targets += @{ GOOS = $currentOS; GOARCH = "amd64"; Name = "filelist$ext" }
    }
    "windows" {
        $targets += @{ GOOS = "windows"; GOARCH = "amd64"; Name = "filelist-windows-amd64.exe" }
    }
    "linux" {
        $targets += @{ GOOS = "linux"; GOARCH = "amd64"; Name = "filelist-linux-amd64" }
    }
    "all" {
        $targets += @{ GOOS = "windows"; GOARCH = "amd64"; Name = "filelist-windows-amd64.exe" }
        $targets += @{ GOOS = "linux";   GOARCH = "amd64"; Name = "filelist-linux-amd64" }
    }
}

# --- Execute builds ---
foreach ($t in $targets) {
    Invoke-Build -GOOS $t.GOOS -GOARCH $t.GOARCH -OutputName $t.Name
}

# --- Reset env ---
$env:GOOS = $null
$env:GOARCH = $null
$env:CGO_ENABLED = $null

# --- Copy config sample ---
$sampleSrc = Join-Path $ScriptDir "config.sample.yaml"
if (Test-Path $sampleSrc) {
    Copy-Item $sampleSrc (Join-Path $BuildDir "config.sample.yaml") -Force
    Write-Host "Copied config.sample.yaml to build/" -ForegroundColor DarkGray
}

Write-Host ""
Write-Host "Done. Build artifacts in build/:" -ForegroundColor Cyan
Get-ChildItem $BuildDir | ForEach-Object {
    Write-Host ("  {0,-40} {1:N1} MB" -f $_.Name, ($_.Length / 1MB))
}
