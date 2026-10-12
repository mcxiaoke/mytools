<#
.SYNOPSIS
  安装 / 卸载 FolderGuard Windows 资源管理器右键层叠菜单。

.DESCRIPTION
  为当前用户 (HKCU) 注册 FolderGuard 层叠上下文菜单：
    - 支持右键点击文件夹 (Directory)
    - 支持在文件夹内空白处右键 (Directory\Background)
    - (已根据安全规范移除 Drive 磁盘根目录，防止对全盘误操作)

  包含两大核心模块：
    1. NoDelete (轻量免提权防误删):
       - Protect Folder
       - Unprotect Folder
       - Toggle Protection
    2. FolderGuard (NTFS 权限锁定，带 UAC 盾牌图标与结果置顶弹窗):
       - NTFS Lock
       - NTFS Lock (Recursive)
       - NTFS Unlock
       - NTFS Reset Permissions

.PARAMETER Uninstall
  卸载层叠菜单，清除当前用户注册表中的所有相关配置。

.PARAMETER TargetDir
  FolderGuard 程序所在目录，默认为 C:\Home\Tools\FolderGuard。

.EXAMPLE
  pwsh -File install-context-menu.ps1
  pwsh -File install-context-menu.ps1 -Uninstall
#>
[CmdletBinding()]
param(
    [switch]$Uninstall,
    [string]$TargetDir = "C:\Home\Tools\FolderGuard"
)

$ErrorActionPreference = 'Stop'

$TargetDir = [Environment]::ExpandEnvironmentVariables($TargetDir).TrimEnd('\')
$NoDeleteGuiExe = Join-Path $TargetDir "NoDeleteGUI.exe"
$FolderGuardRunPs1 = Join-Path $TargetDir "FolderGuard-Run.ps1"

# 注册表目标路径 (不包含 Drive 磁盘根目录)
$baseLocations = @(
    @{ Name = 'Directory';           Param = '"%1"'; RegPath = 'HKCU:\Software\Classes\Directory\shell\FolderGuard' },
    @{ Name = 'DirectoryBackground';  Param = '"%V"'; RegPath = 'HKCU:\Software\Classes\Directory\Background\shell\FolderGuard' }
)

# 历史或遗留需要清理的项 (包含可能遗留的 Drive)
$cleanupLocations = @(
    'HKCU:\Software\Classes\Directory\shell\FolderGuard',
    'HKCU:\Software\Classes\Directory\Background\shell\FolderGuard',
    'HKCU:\Software\Classes\Drive\shell\FolderGuard'
)

# -----------------------------------------------------------------------------
# 卸载逻辑
# -----------------------------------------------------------------------------
if ($Uninstall) {
    Write-Host "[*] 正在卸载 FolderGuard 右键层叠菜单..." -ForegroundColor Yellow
    foreach ($regPath in $cleanupLocations) {
        if (Test-Path $regPath) {
            Remove-Item -Path $regPath -Recurse -Force
            Write-Host "  [-] 已移除注册表项: $regPath" -ForegroundColor Green
        } else {
            Write-Host "  [.] 项不存在，跳过: $regPath" -ForegroundColor Gray
        }
    }
    Write-Host "`n卸载完成！右键菜单已恢复。" -ForegroundColor Cyan
    return
}

# -----------------------------------------------------------------------------
# 安装检查
# -----------------------------------------------------------------------------
Write-Host "[*] 正在验证 FolderGuard 依赖文件..." -ForegroundColor Cyan
if (-not (Test-Path $NoDeleteGuiExe)) {
    Write-Error "找不到 NoDeleteGUI.exe：$NoDeleteGuiExe`n请确保已将二进制文件复制到 $TargetDir"
    return
}
if (-not (Test-Path $FolderGuardRunPs1)) {
    Write-Error "找不到 FolderGuard-Run.ps1：$FolderGuardRunPs1"
    return
}

Write-Host "  [+] NoDeleteGUI.exe: $NoDeleteGuiExe" -ForegroundColor Green
Write-Host "  [+] FolderGuard-Run.ps1: $FolderGuardRunPs1" -ForegroundColor Green

# 确保清除历史遗留的 Drive 项
if (Test-Path 'HKCU:\Software\Classes\Drive\shell\FolderGuard') {
    Remove-Item -Path 'HKCU:\Software\Classes\Drive\shell\FolderGuard' -Recurse -Force -ErrorAction SilentlyContinue
    Write-Host "  [-] 已清理历史遗留的 Drive 磁盘根目录项" -ForegroundColor Yellow
}

# -----------------------------------------------------------------------------
# 菜单项定义
# -----------------------------------------------------------------------------
function Get-MenuItems($pathParam) {
    return @(
        [PSCustomObject]@{
            KeyName      = '01_Protect'
            MUIVerb      = 'Protect Folder'
            Icon         = "$NoDeleteGuiExe,0"
            Command      = "`"$NoDeleteGuiExe`" create $pathParam"
            Separator    = $false
            HasLUAShield = $false
        },
        [PSCustomObject]@{
            KeyName      = '02_Unprotect'
            MUIVerb      = 'Unprotect Folder'
            Icon         = "$NoDeleteGuiExe,0"
            Command      = "`"$NoDeleteGuiExe`" remove $pathParam"
            Separator    = $false
            HasLUAShield = $false
        },
        [PSCustomObject]@{
            KeyName      = '03_Toggle'
            MUIVerb      = 'Toggle Protection'
            Icon         = "$NoDeleteGuiExe,0"
            Command      = "`"$NoDeleteGuiExe`" toggle $pathParam"
            Separator    = $false
            HasLUAShield = $false
        },
        [PSCustomObject]@{
            KeyName      = '04_NTFSLock'
            MUIVerb      = 'NTFS Lock'
            Icon         = ""
            Command      = "powershell.exe -WindowStyle Hidden -ExecutionPolicy Bypass -File `"$FolderGuardRunPs1`" -Action lock -Path $pathParam"
            Separator    = $true
            HasLUAShield = $true
        },
        [PSCustomObject]@{
            KeyName      = '05_NTFSLockRecursive'
            MUIVerb      = 'NTFS Lock (Recursive)'
            Icon         = ""
            Command      = "powershell.exe -WindowStyle Hidden -ExecutionPolicy Bypass -File `"$FolderGuardRunPs1`" -Action lock -Path $pathParam -Recursive"
            Separator    = $false
            HasLUAShield = $true
        },
        [PSCustomObject]@{
            KeyName      = '06_NTFSUnlock'
            MUIVerb      = 'NTFS Unlock'
            Icon         = ""
            Command      = "powershell.exe -WindowStyle Hidden -ExecutionPolicy Bypass -File `"$FolderGuardRunPs1`" -Action unlock -Path $pathParam"
            Separator    = $false
            HasLUAShield = $true
        },
        [PSCustomObject]@{
            KeyName      = '07_NTFSReset'
            MUIVerb      = 'NTFS Reset Permissions'
            Icon         = ""
            Command      = "powershell.exe -WindowStyle Hidden -ExecutionPolicy Bypass -File `"$FolderGuardRunPs1`" -Action unlockall -Path $pathParam"
            Separator    = $false
            HasLUAShield = $true
        }
    )
}

# -----------------------------------------------------------------------------
# 写入注册表
# -----------------------------------------------------------------------------
Write-Host "`n[*] 正在向 HKCU 写入右键层叠菜单..." -ForegroundColor Cyan

foreach ($loc in $baseLocations) {
    $rootKey = $loc.RegPath
    Write-Host "`n[配置作用域] $($loc.Name) -> $rootKey" -ForegroundColor Yellow

    # 创建或重置主键
    if (-not (Test-Path $rootKey)) {
        New-Item -Path $rootKey -Force | Out-Null
    }

    Set-ItemProperty -Path $rootKey -Name 'MUIVerb' -Value 'FolderGuard' -Type String
    Set-ItemProperty -Path $rootKey -Name 'Icon' -Value "$NoDeleteGuiExe,0" -Type String
    Set-ItemProperty -Path $rootKey -Name 'SubCommands' -Value '' -Type String

    # 子菜单容器
    $shellKey = Join-Path $rootKey 'shell'
    if (-not (Test-Path $shellKey)) {
        New-Item -Path $shellKey -Force | Out-Null
    }

    $items = Get-MenuItems $loc.Param
    foreach ($item in $items) {
        $subKey = Join-Path $shellKey $item.KeyName
        if (-not (Test-Path $subKey)) {
            New-Item -Path $subKey -Force | Out-Null
        }

        Set-ItemProperty -Path $subKey -Name 'MUIVerb' -Value $item.MUIVerb -Type String

        if ($item.Icon) {
            Set-ItemProperty -Path $subKey -Name 'Icon' -Value $item.Icon -Type String
        } else {
            Remove-ItemProperty -Path $subKey -Name 'Icon' -ErrorAction SilentlyContinue
        }

        if ($item.Separator) {
            Set-ItemProperty -Path $subKey -Name 'CommandFlags' -Value 32 -Type DWord
        } else {
            Remove-ItemProperty -Path $subKey -Name 'CommandFlags' -ErrorAction SilentlyContinue
        }

        if ($item.HasLUAShield) {
            Set-ItemProperty -Path $subKey -Name 'HasLUAShield' -Value '' -Type String
        } else {
            Remove-ItemProperty -Path $subKey -Name 'HasLUAShield' -ErrorAction SilentlyContinue
        }

        # command 子项
        $cmdKey = Join-Path $subKey 'command'
        if (-not (Test-Path $cmdKey)) {
            New-Item -Path $cmdKey -Force | Out-Null
        }
        Set-ItemProperty -Path $cmdKey -Name '(Default)' -Value $item.Command -Type String
        Write-Host "  [+] $($item.MUIVerb)" -ForegroundColor Gray
    }
}

# -----------------------------------------------------------------------------
# 生成 .reg 注册表备份文件便于留存
# -----------------------------------------------------------------------------
$tempDir = Join-Path $PSScriptRoot 'temp'
if (-not (Test-Path $tempDir)) {
    New-Item -ItemType Directory -Path $tempDir -Force | Out-Null
}

$regExportPath = Join-Path $tempDir 'FolderGuard_ContextMenu.reg'
$escTarget = $TargetDir.Replace('\', '\\')
$escNoDelete = $NoDeleteGuiExe.Replace('\', '\\')
$escRun = $FolderGuardRunPs1.Replace('\', '\\')

$regContent = @"
Windows Registry Editor Version 5.00

; ==============================================================================
; FolderGuard Context Menu (Directory, Directory\Background)
; (Drive root removed for system safety)
; ==============================================================================

[HKEY_CURRENT_USER\Software\Classes\Directory\shell\FolderGuard]
"MUIVerb"="FolderGuard"
"Icon"="$escNoDelete,0"
"SubCommands"=""

[HKEY_CURRENT_USER\Software\Classes\Directory\shell\FolderGuard\shell\01_Protect]
"MUIVerb"="Protect Folder"
"Icon"="$escNoDelete,0"
[HKEY_CURRENT_USER\Software\Classes\Directory\shell\FolderGuard\shell\01_Protect\command]
@="\"$escNoDelete\" create \"%1\""

[HKEY_CURRENT_USER\Software\Classes\Directory\shell\FolderGuard\shell\02_Unprotect]
"MUIVerb"="Unprotect Folder"
"Icon"="$escNoDelete,0"
[HKEY_CURRENT_USER\Software\Classes\Directory\shell\FolderGuard\shell\02_Unprotect\command]
@="\"$escNoDelete\" remove \"%1\""

[HKEY_CURRENT_USER\Software\Classes\Directory\shell\FolderGuard\shell\03_Toggle]
"MUIVerb"="Toggle Protection"
"Icon"="$escNoDelete,0"
[HKEY_CURRENT_USER\Software\Classes\Directory\shell\FolderGuard\shell\03_Toggle\command]
@="\"$escNoDelete\" toggle \"%1\""

[HKEY_CURRENT_USER\Software\Classes\Directory\shell\FolderGuard\shell\04_NTFSLock]
"MUIVerb"="NTFS Lock"
"CommandFlags"=dword:00000020
"HasLUAShield"=""
[HKEY_CURRENT_USER\Software\Classes\Directory\shell\FolderGuard\shell\04_NTFSLock\command]
@="powershell.exe -WindowStyle Hidden -ExecutionPolicy Bypass -File \"$escRun\" -Action lock -Path \"%1\""

[HKEY_CURRENT_USER\Software\Classes\Directory\shell\FolderGuard\shell\05_NTFSLockRecursive]
"MUIVerb"="NTFS Lock (Recursive)"
"HasLUAShield"=""
[HKEY_CURRENT_USER\Software\Classes\Directory\shell\FolderGuard\shell\05_NTFSLockRecursive\command]
@="powershell.exe -WindowStyle Hidden -ExecutionPolicy Bypass -File \"$escRun\" -Action lock -Path \"%1\" -Recursive"

[HKEY_CURRENT_USER\Software\Classes\Directory\shell\FolderGuard\shell\06_NTFSUnlock]
"MUIVerb"="NTFS Unlock"
"HasLUAShield"=""
[HKEY_CURRENT_USER\Software\Classes\Directory\shell\FolderGuard\shell\06_NTFSUnlock\command]
@="powershell.exe -WindowStyle Hidden -ExecutionPolicy Bypass -File \"$escRun\" -Action unlock -Path \"%1\""

[HKEY_CURRENT_USER\Software\Classes\Directory\shell\FolderGuard\shell\07_NTFSReset]
"MUIVerb"="NTFS Reset Permissions"
"HasLUAShield"=""
[HKEY_CURRENT_USER\Software\Classes\Directory\shell\FolderGuard\shell\07_NTFSReset\command]
@="powershell.exe -WindowStyle Hidden -ExecutionPolicy Bypass -File \"$escRun\" -Action unlockall -Path \"%1\""

; --- Directory Background ---
[HKEY_CURRENT_USER\Software\Classes\Directory\Background\shell\FolderGuard]
"MUIVerb"="FolderGuard"
"Icon"="$escNoDelete,0"
"SubCommands"=""

[HKEY_CURRENT_USER\Software\Classes\Directory\Background\shell\FolderGuard\shell\01_Protect]
"MUIVerb"="Protect Folder"
"Icon"="$escNoDelete,0"
[HKEY_CURRENT_USER\Software\Classes\Directory\Background\shell\FolderGuard\shell\01_Protect\command]
@="\"$escNoDelete\" create \"%V\""

[HKEY_CURRENT_USER\Software\Classes\Directory\Background\shell\FolderGuard\shell\02_Unprotect]
"MUIVerb"="Unprotect Folder"
"Icon"="$escNoDelete,0"
[HKEY_CURRENT_USER\Software\Classes\Directory\Background\shell\FolderGuard\shell\02_Unprotect\command]
@="\"$escNoDelete\" remove \"%V\""

[HKEY_CURRENT_USER\Software\Classes\Directory\Background\shell\FolderGuard\shell\03_Toggle]
"MUIVerb"="Toggle Protection"
"Icon"="$escNoDelete,0"
[HKEY_CURRENT_USER\Software\Classes\Directory\Background\shell\FolderGuard\shell\03_Toggle\command]
@="\"$escNoDelete\" toggle \"%V\""

[HKEY_CURRENT_USER\Software\Classes\Directory\Background\shell\FolderGuard\shell\04_NTFSLock]
"MUIVerb"="NTFS Lock"
"CommandFlags"=dword:00000020
"HasLUAShield"=""
[HKEY_CURRENT_USER\Software\Classes\Directory\Background\shell\FolderGuard\shell\04_NTFSLock\command]
@="powershell.exe -WindowStyle Hidden -ExecutionPolicy Bypass -File \"$escRun\" -Action lock -Path \"%V\""

[HKEY_CURRENT_USER\Software\Classes\Directory\Background\shell\FolderGuard\shell\05_NTFSLockRecursive]
"MUIVerb"="NTFS Lock (Recursive)"
"HasLUAShield"=""
[HKEY_CURRENT_USER\Software\Classes\Directory\Background\shell\FolderGuard\shell\05_NTFSLockRecursive\command]
@="powershell.exe -WindowStyle Hidden -ExecutionPolicy Bypass -File \"$escRun\" -Action lock -Path \"%V\" -Recursive"

[HKEY_CURRENT_USER\Software\Classes\Directory\Background\shell\FolderGuard\shell\06_NTFSUnlock]
"MUIVerb"="NTFS Unlock"
"HasLUAShield"=""
[HKEY_CURRENT_USER\Software\Classes\Directory\Background\shell\FolderGuard\shell\06_NTFSUnlock\command]
@="powershell.exe -WindowStyle Hidden -ExecutionPolicy Bypass -File \"$escRun\" -Action unlock -Path \"%V\""

[HKEY_CURRENT_USER\Software\Classes\Directory\Background\shell\FolderGuard\shell\07_NTFSReset]
"MUIVerb"="NTFS Reset Permissions"
"HasLUAShield"=""
[HKEY_CURRENT_USER\Software\Classes\Directory\Background\shell\FolderGuard\shell\07_NTFSReset\command]
@="powershell.exe -WindowStyle Hidden -ExecutionPolicy Bypass -File \"$escRun\" -Action unlockall -Path \"%V\""
"@

[System.IO.File]::WriteAllText($regExportPath, $regContent, [System.Text.Encoding]::Unicode)
Write-Host "`n[+] 注册表备份脚本已生成: $regExportPath" -ForegroundColor DarkGray

Write-Host @"

============================================================
安装成功！
现在可以在 Windows 资源管理器中测试：
  1. 右键任意普通文件夹 -> 「FolderGuard」-> 查看 7 个层叠子菜单
  2. 进入文件夹内部空白处右键 -> 「FolderGuard」

已安全移除 Drive 磁盘根目录项，杜绝全盘误操作与卡死。
若需卸载恢复：
  pwsh -File install-context-menu.ps1 -Uninstall
============================================================
"@ -ForegroundColor Green
