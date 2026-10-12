param (
    [Parameter(Mandatory=$false, Position=0)]
    [ValidateSet("lock", "unlock", "unlockall")]
    [string]$Action,

    [Parameter(Mandatory=$false, Position=1)]
    [string]$Path,

    [Alias("r")]
    [switch]$Recursive,

    [Alias("y")]
    [switch]$Force
)

# --- 1. 纯粹的权限检查 ---
$currentPrincipal = New-Object Security.Principal.WindowsPrincipal([Security.Principal.WindowsIdentity]::GetCurrent())
if (-not $currentPrincipal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
    Write-Host "[错误] 权限不足" -ForegroundColor Red
    Write-Host "请以管理员身份运行此脚本。"
    if (-not $Force) { Read-Host "按回车键退出..." }
    exit
}

# --- 2. 基础配置 ---
$StatusFileName = "FOLDER_LOCKED.txt"

if (-not $Action -or -not $Path) {
    Write-Host "用法: .\FolderGuard.ps1 lock 路径 [-r] [-y]"
    exit
}

try {
    $AbsolutePath = (Resolve-Path $Path -ErrorAction Stop).ProviderPath
    $StatusFilePath = Join-Path $AbsolutePath $StatusFileName
} catch {
    Write-Host "错误: 找不到路径 $Path" -ForegroundColor Red
    if (-not $Force) { Read-Host "按回车键退出..." }
    exit
}

# --- 备忘：对于海量文件(如 >10万项)，建议改用 icacls 绕过 PowerShell 对象扫描 ---
# [icacls 实现参考]
# 非递归锁定 (对应 $Recursive=$false):
#   icacls "路径" /deny "${env:UserName}:(DE,DC)"
#   注：DE (Delete) = 删除本身, DC (Delete Child) = 删除子项
#
# 递归锁定 (对应 $Recursive=$true):
#   icacls "路径" /deny "${env:UserName}:(OI)(CI)(DE,DC)"
#   注：OI (Object Inherit) = 文件继承, CI (Container Inherit) = 文件夹继承
#
# 彻底解锁 (对应 unlockall):
#   icacls "路径" /remove:d "${env:UserName}"
# ---------------------------------------------------------------------------

# 以下是你现有的 PowerShell 对象定义：
$ArDir = New-Object System.Security.AccessControl.FileSystemAccessRule($env:UserName, "Delete, DeleteSubdirectoriesAndFiles", "None", "None", "Deny")
$ArFile = New-Object System.Security.AccessControl.FileSystemAccessRule($env:UserName, "Delete", "None", "None", "Deny")

if ($Recursive) {
    # 开启继承标志，这在海量文件下会导致 Set-Acl 极其缓慢
    $ArDir = New-Object System.Security.AccessControl.FileSystemAccessRule($env:UserName, "Delete, DeleteSubdirectoriesAndFiles", "ContainerInherit, ObjectInherit", "None", "Deny")
}

# --- 3. 核心业务逻辑 ---
switch ($Action) {
    "lock" {
        if (-not $Force) {
            Write-Host "目标: $AbsolutePath" -ForegroundColor Yellow
            $Confirm = Read-Host "确认锁定? (Y/N)"
            if ($Confirm -ne "Y") { exit }
        }

        $Timestamp = Get-Date -Format "yyyy-MM-dd HH:mm:ss"
        $RStatus = if ($Recursive) { "是 (递归)" } else { "否" }
        $Content = "========================================`r`n" +
                   "       文件夹锁定状态报告 (LOCKED)`r`n" +
                   "========================================`r`n" +
                   "锁定时间: $Timestamp`r`n" +
                   "递归锁定: $RStatus`r`n" +
                   "锁定路径: $AbsolutePath`r`n" +
                   "操作人员: $env:UserName`r`n" +
                   "========================================`r`n" +
                   "说明: 此文件夹已通过 NTFS 权限锁定。`r`n" +
                   "========================================"
        try {
            if (Test-Path $StatusFilePath) {
                $acl = Get-Acl $StatusFilePath
                $acl.RemoveAccessRule($ArFile)
                Set-Acl $StatusFilePath $acl -ErrorAction SilentlyContinue
                Set-ItemProperty $StatusFilePath Attributes Normal -ErrorAction SilentlyContinue
            }
            
            $Content | Out-File $StatusFilePath -Encoding UTF8 -Force
            Set-ItemProperty $StatusFilePath Attributes ReadOnly
            $fAcl = Get-Acl $StatusFilePath
            $fAcl.AddAccessRule($ArFile)
            Set-Acl $StatusFilePath $fAcl

            $dAcl = Get-Acl $AbsolutePath
            $dAcl.AddAccessRule($ArDir)
            Set-Acl $AbsolutePath $dAcl

            Write-Host "成功: 文件夹已锁定。" -ForegroundColor Cyan
        } catch {
            Write-Host "执行出错: $($_.Exception.Message)" -ForegroundColor Red
            if (-not $Force) { Read-Host "按回车键查看错误..." }
        }
    }

    "unlock" {
        try {
            $dAcl = Get-Acl $AbsolutePath
            $dAcl.RemoveAccessRule($ArDir)
            $ArDirRecursive = New-Object System.Security.AccessControl.FileSystemAccessRule($env:UserName, "Delete, DeleteSubdirectoriesAndFiles", "ContainerInherit, ObjectInherit", "None", "Deny")
            $dAcl.RemoveAccessRule($ArDirRecursive)
            Set-Acl $AbsolutePath $dAcl

            if (Test-Path $StatusFilePath) {
                $fAcl = Get-Acl $StatusFilePath
                $fAcl.RemoveAccessRule($ArFile)
                Set-Acl $StatusFilePath $fAcl -ErrorAction SilentlyContinue
                Set-ItemProperty $StatusFilePath Attributes Normal -ErrorAction SilentlyContinue
                Remove-Item $StatusFilePath -Force
            }
            Write-Host "成功: 锁定已解除。" -ForegroundColor Green
        } catch {
            Write-Host "解锁出错: $($_.Exception.Message)" -ForegroundColor Red
        }
    }

    "unlockall" {
        $dAcl = Get-Acl $AbsolutePath
        $dAcl.Access | Where-Object { $_.AccessControlType -eq "Deny" } | ForEach-Object { $dAcl.RemoveAccessRuleSpecific($_) }
        Set-Acl $AbsolutePath $dAcl
        
        if (Test-Path $StatusFilePath) {
            Set-ItemProperty $StatusFilePath Attributes Normal -ErrorAction SilentlyContinue
            Remove-Item $StatusFilePath -Force -ErrorAction SilentlyContinue
        }
        Write-Host "强制清理完成。" -ForegroundColor Yellow
    }
}

if ($Force) { Start-Sleep -Seconds 2 }




# ===========================================================================
#                      ICACLS 权限管理速查手册 (学习备忘)
# ===========================================================================
# icacls 是 Windows 原生 C++ 命令行工具，处理海量文件（如 >10万个）速度极快。
#
# 1. 核心权限缩写 (Permissions):
#    DE - Delete (删除对象本身)
#    DC - Delete Child (删除子文件/子文件夹)
#    F  - Full Access (完全控制)
#    M  - Modify (修改权限，含读/写/删)
#    RX - Read & Execute (读取和执行)
#
# 2. 继承标志 (Inheritance Flags):
#    (OI) - Object Inherit: 权限向下传递给【文件】
#    (CI) - Container Inherit: 权限向下传递给【子文件夹】
#    (IO) - Inherit Only: 权限不应用给当前目录，仅传给后代
#    [无标志] - 仅应用到当前文件夹本身（实现海量文件“秒锁”的关键）
#
# 3. 常用操作指令:
#
#    [锁定] 仅锁定当前文件夹（防删/防重命名）：
#    icacls "路径" /deny %username%:(DE,DC)
#
#    [递归锁定] 锁定文件夹及其内部所有内容：
#    icacls "路径" /deny %username%:(OI)(CI)(DE,DC)
#
#    [解锁] 移除对特定用户的 Deny 规则：
#    icacls "路径" /remove:d %username%
#
#    [强制重置] 权限改乱时的救命稻草（恢复继承）：
#    icacls "路径" /reset /t /c /l
#
# 4. 备份与恢复 (非常实用):
#    备份：icacls "D:\Data" /save AclBackup.txt /t
#    还原：icacls "D:\" /restore AclBackup.txt
#
# 5. 常用参数:
#    /t : 递归处理所有子目录/文件
#    /c : 忽略错误继续执行（如遇到正在占用的文件）
#    /q : 静默模式（不显示成功信息）
# ===========================================================================