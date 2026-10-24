<#
.SYNOPSIS
    多技术栈工程构建与缓存精准清理工具 PowerShell 启动器
.DESCRIPTION
    包装并调用 clean_projects.py，支持 .NET, Rust, Flutter, Node.js, Android, CMake, Python 等项目的临时目录识别与清理。
.EXAMPLE
    .\clean_projects.ps1 -Path C:\Home\Projects
    .\clean_projects.ps1 -Path C:\Home\Projects -SkipNodeModules
    .\clean_projects.ps1 -Path C:\Home\Projects -Types "rust,dotnet"
    .\clean_projects.ps1 -Path C:\Home\Projects -Delete
#>

[CmdletBinding()]
param (
    [Parameter(Position = 0)]
    [string]$Path = ".",

    [switch]$DryRun,
    [switch]$Delete,
    [switch]$Yes,
    [string]$Types,
    [switch]$SkipNodeModules,
    [string]$MinSize,
    [int]$MaxDepth,
    [int]$Workers = 8
)

$scriptDir = Split-Path -Parent $MyInvocation.MyCommand.Path
$pyScript = Join-Path $scriptDir "clean_projects.py"

$pythonCmd = (Get-Command python -ErrorAction SilentlyContinue)?.Source
if (-not $pythonCmd) {
    Write-Error "未找到 python 解释器，请确保 Python 已加入 PATH 环境变量。"
    exit 1
}

$argsList = @($pyScript, "--path", $Path)

if ($Delete) {
    $argsList += "--delete"
}
if ($Yes) {
    $argsList += "--yes"
}
if ($Types) {
    $argsList += "--types", $Types
}
if ($SkipNodeModules) {
    $argsList += "--skip-node-modules"
}
if ($MinSize) {
    $argsList += "--min-size", $MinSize
}
if ($MaxDepth) {
    $argsList += "--max-depth", $MaxDepth
}
if ($Workers) {
    $argsList += "--workers", $Workers
}

& $pythonCmd $argsList
