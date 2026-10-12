<#
.SYNOPSIS
  FolderGuard-Run.ps1 - 加固版 GUI 运行器
#>
[CmdletBinding()]
param (
    [Parameter(Mandatory=$true, Position=0)]
    [ValidateSet("lock", "unlock", "unlockall")]
    [string]$Action,

    [Parameter(Mandatory=$true, Position=1)]
    [string]$Path,

    [Alias("r")]
    [switch]$Recursive
)

$ErrorActionPreference = 'Stop'

function Show-Dialog {
    param(
        [string]$Message,
        [string]$Title = "FolderGuard",
        [ValidateSet("Info", "Error", "Warning")]
        [string]$Icon = "Info"
    )
    Add-Type -AssemblyName System.Windows.Forms
    $iconType = switch ($Icon) {
        "Error"   { [System.Windows.Forms.MessageBoxIcon]::Error }
        "Warning" { [System.Windows.Forms.MessageBoxIcon]::Warning }
        default   { [System.Windows.Forms.MessageBoxIcon]::Information }
    }
    # 使用 DefaultDesktopOnly 确保弹窗置顶，绝不被 Explorer 遮挡
    [System.Windows.Forms.MessageBox]::Show(
        $Message,
        $Title,
        [System.Windows.Forms.MessageBoxButtons]::OK,
        $iconType,
        [System.Windows.Forms.MessageBoxDefaultButton]::Button1,
        [System.Windows.Forms.MessageBoxOptions]::DefaultDesktopOnly
    ) | Out-Null
}

function Test-IsAdmin {
    $id = [Security.Principal.WindowsIdentity]::GetCurrent()
    return (New-Object Security.Principal.WindowsPrincipal $id).IsInRole(
        [Security.Principal.WindowsBuiltInRole]::Administrator)
}

# 1. 规范化路径
try {
    $resolvedPath = (Resolve-Path -LiteralPath $Path -ErrorAction Stop).ProviderPath
} catch {
    Show-Dialog "Target path not found:`n$Path" "FolderGuard - Error" "Error"
    exit 1
}

# 2. 安全防线：严禁对磁盘根目录操作 (如 C:\, D:\)
$rootPath = [System.IO.Path]::GetPathRoot($resolvedPath)
if ($resolvedPath.TrimEnd('\') -eq $rootPath.TrimEnd('\')) {
    Show-Dialog "Operating directly on drive root ($resolvedPath) is blocked for safety to prevent system lockup and permission errors." "FolderGuard - Safety Warning" "Warning"
    exit 1
}

# 3. 提权检查与自动提权
if (-not (Test-IsAdmin)) {
    $argList = @(
        '-NoProfile',
        '-WindowStyle', 'Hidden',
        '-ExecutionPolicy', 'Bypass',
        '-File', "`"$PSCommandPath`"",
        '-Action', $Action,
        '-Path', "`"$resolvedPath`""
    )
    if ($Recursive) { $argList += '-Recursive' }

    try {
        Start-Process -FilePath 'powershell.exe' -Verb RunAs -ArgumentList $argList -WindowStyle Hidden
        exit 0
    } catch {
        Show-Dialog "Administrator privileges required to execute NTFS actions.`nOperation cancelled." "FolderGuard - Permission Denied" "Warning"
        exit 1
    }
}

# 4. 执行核心 NTFS 锁定/解锁逻辑 (完全自包含，无任何 Read-Host/Sleep 挂起)
$StatusFileName = "FOLDER_LOCKED.txt"
$StatusFilePath = Join-Path $resolvedPath $StatusFileName

$currentUser = [Environment]::UserName
$ArDirNone = New-Object System.Security.AccessControl.FileSystemAccessRule(
    $currentUser, "Delete, DeleteSubdirectoriesAndFiles", "None", "None", "Deny"
)
$ArDirRec = New-Object System.Security.AccessControl.FileSystemAccessRule(
    $currentUser, "Delete, DeleteSubdirectoriesAndFiles", "ContainerInherit, ObjectInherit", "None", "Deny"
)
$ArFile = New-Object System.Security.AccessControl.FileSystemAccessRule(
    $currentUser, "Delete", "None", "None", "Deny"
)

try {
    switch ($Action) {
        "lock" {
            $ArDir = if ($Recursive) { $ArDirRec } else { $ArDirNone }

            # 处理状态文件
            if (Test-Path $StatusFilePath) {
                try {
                    $acl = Get-Acl $StatusFilePath
                    $acl.RemoveAccessRule($ArFile)
                    Set-Acl $StatusFilePath $acl -ErrorAction SilentlyContinue
                    Set-ItemProperty $StatusFilePath Attributes Normal -ErrorAction SilentlyContinue
                } catch {}
            }

            $timestamp = Get-Date -Format "yyyy-MM-dd HH:mm:ss"
            $rStatus = if ($Recursive) { "Yes (Inherited to sub-items)" } else { "No (Directory only)" }
            $content = @"
========================================
       FolderGuard Protection Report
========================================
Locked Time : $timestamp
Recursive   : $rStatus
Target Path : $resolvedPath
Locked By   : $currentUser
========================================
Protected by NTFS Access Control Deny Rule.
========================================
"@
            [System.IO.File]::WriteAllText($StatusFilePath, $content, [System.Text.Encoding]::UTF8)
            Set-ItemProperty $StatusFilePath Attributes ReadOnly -ErrorAction SilentlyContinue
            try {
                $fAcl = Get-Acl $StatusFilePath
                $fAcl.AddAccessRule($ArFile)
                Set-Acl $StatusFilePath $fAcl -ErrorAction SilentlyContinue
            } catch {}

            # 应用目录权限
            $dAcl = Get-Acl $resolvedPath
            $dAcl.AddAccessRule($ArDir)
            Set-Acl $resolvedPath $dAcl

            $msg = "Folder locked successfully (NTFS Access Denied).`n`nPath:`n$resolvedPath`n`nRecursive: $rStatus"
            Show-Dialog $msg "FolderGuard -- NTFS Protection" "Info"
        }

        "unlock" {
            $dAcl = Get-Acl $resolvedPath
            $dAcl.RemoveAccessRule($ArDirNone)
            $dAcl.RemoveAccessRule($ArDirRec)
            Set-Acl $resolvedPath $dAcl

            if (Test-Path $StatusFilePath) {
                try {
                    $fAcl = Get-Acl $StatusFilePath
                    $fAcl.RemoveAccessRule($ArFile)
                    Set-Acl $StatusFilePath $fAcl -ErrorAction SilentlyContinue
                    Set-ItemProperty $StatusFilePath Attributes Normal -ErrorAction SilentlyContinue
                    Remove-Item $StatusFilePath -Force -ErrorAction SilentlyContinue
                } catch {}
            }

            $msg = "Folder unlocked successfully.`n`nPath:`n$resolvedPath"
            Show-Dialog $msg "FolderGuard -- NTFS Protection" "Info"
        }

        "unlockall" {
            $dAcl = Get-Acl $resolvedPath
            $denyRules = @($dAcl.Access | Where-Object { $_.AccessControlType -eq "Deny" })
            foreach ($rule in $denyRules) {
                $dAcl.RemoveAccessRuleSpecific($rule)
            }
            Set-Acl $resolvedPath $dAcl

            if (Test-Path $StatusFilePath) {
                try {
                    Set-ItemProperty $StatusFilePath Attributes Normal -ErrorAction SilentlyContinue
                    Remove-Item $StatusFilePath -Force -ErrorAction SilentlyContinue
                } catch {}
            }

            $msg = "All NTFS Deny rules cleared and permissions reset.`n`nPath:`n$resolvedPath"
            Show-Dialog $msg "FolderGuard -- NTFS Protection" "Info"
        }
    }
} catch {
    Show-Dialog "Operation failed:`n$($_.Exception.Message)`n`nPath:`n$resolvedPath" "FolderGuard - Error" "Error"
    exit 1
}
