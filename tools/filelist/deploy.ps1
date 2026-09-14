<#
.SYNOPSIS
  一键编译并更新部署 FileList 到远程 Linux 服务器。

.DESCRIPTION
  1. 交叉编译 Linux amd64 二进制 (build/filelist-linux-amd64)
  2. SCP 上传到远程服务器临时目录
  3. 原子替换 /usr/local/bin/filelist（使用 install 命令，避免 Linux "Text file busy" 问题）
  4. 重启 systemd 服务并检查运行状态

.EXAMPLE
  .\deploy.ps1
  .\deploy.ps1 -HostTarget "root@192.168.1.118"
#>

[CmdletBinding()]
param(
  [string]$HostTarget = "root@192.168.1.118",
  [string]$ServiceName = "filelist",
  [string]$RemoteBin = "/usr/local/bin/filelist"
)

$ErrorActionPreference = 'Stop'
$projectRoot = $PSScriptRoot
$buildScript = Join-Path $projectRoot 'build.ps1'
$linuxBin = Join-Path $projectRoot 'build\filelist-linux-amd64'

Write-Host "=== Deploying FileList to $HostTarget ===" -ForegroundColor Cyan

# 1. 编译 Linux 二进制
Write-Host "[1/3] Compiling Linux binary..." -ForegroundColor Yellow
& $buildScript -Target linux
if (-not (Test-Path $linuxBin)) {
  Write-Error "Build failed: $linuxBin not found."
}

# 2. 上传二进制到远程临时目录
Write-Host "[2/3] Uploading binary to $HostTarget..." -ForegroundColor Yellow
scp $linuxBin "${HostTarget}:/tmp/filelist"

# 3. 原子更新并重启 systemd
Write-Host "[3/3] Updating binary and restarting $ServiceName..." -ForegroundColor Yellow
$remoteCmd = "install -m 755 /tmp/filelist $RemoteBin && rm -f /tmp/filelist && systemctl restart $ServiceName && systemctl status $ServiceName --no-pager"
ssh $HostTarget $remoteCmd

Write-Host "`n✔ Successfully deployed and restarted $ServiceName on $HostTarget!" -ForegroundColor Green
