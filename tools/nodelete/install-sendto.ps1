<#
.SYNOPSIS
  在「发送到」菜单中安装 / 卸载 NoDelete 快捷方式。

.DESCRIPTION
  「发送到」菜单本质上就是用户 SendTo 文件夹里的快捷方式。资源管理器会把
  选中的文件/文件夹路径作为命令行第一个参数传给目标程序，因此本工具只需
  接收一个路径参数即可。

  创建三个菜单项：
    禁止删除      -> NoDeleteGUI.exe create "<路径>"
    允许删除      -> NoDeleteGUI.exe remove "<路径>"
    切换保护状态  -> NoDeleteGUI.exe toggle "<路径>"

.PARAMETER Uninstall
  存在时执行卸载，删除本脚本创建的快捷方式。

.PARAMETER ExePath
  指定 NoDeleteGUI.exe 路径，默认为本项目 build\NoDeleteGUI.exe。

.EXAMPLE
  powershell -ExecutionPolicy Bypass -File install-sendto.ps1
  powershell -ExecutionPolicy Bypass -File install-sendto.ps1 -Uninstall
#>
[CmdletBinding()]
param(
    [switch]$Uninstall,
    [string]$ExePath
)

$ErrorActionPreference = 'Stop'

# 记录用户是否显式指定了路径（提权重启时需要原样传递）
$ExePathExplicit = [bool]$ExePath

if (-not $ExePath) {
    $ExePath = Join-Path $PSScriptRoot 'build\NoDeleteGUI.exe'
}
$ExePath = [Environment]::ExpandEnvironmentVariables($ExePath)

# SendTo 目录（跟随当前用户漫游）
$sendTo = [Environment]::GetFolderPath('SendTo')
if (-not (Test-Path $sendTo)) {
    New-Item -ItemType Directory -Path $sendTo -Force | Out-Null
}

# 菜单项定义：显示名 -> 传给程序的动词
# 用 PSCustomObject 而非 Hashtable：Hashtable 的 .Name 会被解析成
# 自带的 Name 属性（显示为 System.Collections.Hashtable.Name），取不到键值。
$entries = @(
    [PSCustomObject]@{ Name = '禁止删除';     Verb = 'create' }
    [PSCustomObject]@{ Name = '允许删除';     Verb = 'remove' }
    [PSCustomObject]@{ Name = '切换保护状态'; Verb = 'toggle' }
)

# 统一的清理函数
function Remove-Entry([string]$name) {
    $lnk = Join-Path $sendTo ($name + '.lnk')
    if (Test-Path $lnk) {
        Remove-Item $lnk -Force
        Write-Host "已移除菜单项: $name"
    }
}

# ---- 权限自提升 ------------------------------------------------------
# 「发送到」目录在 UAC 启用时属于受保护位置：
#   - 新建文件允许
#   - 但删除 / 覆盖已存在的 .lnk 会报 Access denied
# 所以探测必须针对"已存在的文件"，新建+删除自己造的文件是测不出来的。
function Test-CanModifyExisting {
    # 找一个当前用户有权读取的既有文件作为探测对象；
    # 没有则退回新建/删除探测（此时多半也没问题）。
    $victim = $null
    foreach ($n in @('禁止删除', '允许删除', '切换保护状态')) {
        $p = Join-Path $sendTo ($n + '.lnk')
        if (Test-Path $p) { $victim = $p; break }
    }
    if (-not $victim) {
        $victim = Get-ChildItem -LiteralPath $sendTo -Filter '*.lnk' -ErrorAction SilentlyContinue |
                  Select-Object -First 1 -ExpandProperty FullName
    }
    if (-not $victim) { return $true }   # 目录里没有任何既有文件，装新文件不受限

    try {
        # 只读取内容并尝试原样写回：能写回说明有修改权限
        $bytes = [IO.File]::ReadAllBytes($victim)
        [IO.File]::WriteAllBytes($victim, $bytes)
        return $true
    } catch {
        return $false
    }
}

function Test-IsAdmin {
    $id = [Security.Principal.WindowsIdentity]::GetCurrent()
    return (New-Object Security.Principal.WindowsPrincipal $id).IsInRole(
        [Security.Principal.WindowsBuiltInRole]::Administrator)
}

# 只在"确实要改动已有快捷方式"时检查；纯安装全新名称时不打扰用户。
$hasExisting = $false
foreach ($e in $entries) {
    if (Test-Path (Join-Path $sendTo ($e.Name + '.lnk'))) { $hasExisting = $true; break }
}

if (($Uninstall -or $hasExisting) -and -not (Test-IsAdmin) -and -not (Test-CanModifyExisting)) {
    Write-Host "「发送到」目录需要管理员权限才能修改，正在请求提升..." -ForegroundColor Yellow
    $verb = if ($Uninstall) { '-Uninstall' } else { '' }
    $argList = @('-NoProfile', '-ExecutionPolicy', 'Bypass', '-File', "`"$PSCommandPath`"")
    if ($ExePathExplicit) {
        $argList += @('-ExePath', "`"$ExePath`"")
    }
    if ($verb) { $argList += $verb }

    try {
        $p = Start-Process -FilePath 'powershell.exe' -Verb RunAs -ArgumentList $argList -Wait -PassThru
        exit $p.ExitCode
    } catch {
        Write-Error @"
提权失败：$($_.Exception.Message)

请右键以「管理员身份运行」本脚本，或手动删除 SendTo 目录中的旧快捷方式后重试。
"@
        exit 1
    }
}

if ($Uninstall) {
    foreach ($e in $entries) { Remove-Entry $e.Name }
    Write-Host "`n卸载完成。"
    return
}

# 安装前检查可执行文件是否存在
if (-not (Test-Path $ExePath)) {
    Write-Error @"
找不到 NoDeleteGUI.exe：
  $ExePath

请先编译：
  ./build.sh
或用 -ExePath 指定路径。
"@
    return
}

$shell = New-Object -ComObject WScript.Shell

foreach ($e in $entries) {
    $lnkPath = Join-Path $sendTo ($e.Name + '.lnk')

    $sc = $shell.CreateShortcut($lnkPath)
    $sc.TargetPath       = $ExePath
    # 关键：%1 由资源管理器替换为用户选中的路径
    $sc.Arguments        = '{0} "%1"' -f $e.Verb
    $sc.WorkingDirectory = Split-Path -Parent $ExePath
    $sc.IconLocation     = "$ExePath,0"
    $sc.Description      = 'NoDelete - 目录删除保护'
    $sc.Save()

    # 注意：字符串里写 "$e.Name" 会被解析成 "$e" + 字面量 ".Name"，
    # 必须用 $() 子表达式才能取到属性。
    Write-Host ("已安装菜单项: {0}  ->  {1} ""%1""" -f $e.Name, $e.Verb)
}

Write-Host "`n安装完成。现在可以在资源管理器中："
Write-Host "  右键点击文件夹 -> 发送到 -> 选择上面任一菜单项。"
Write-Host "提示：若菜单未刷新，重启资源管理器（任务管理器 -> 重新启动「Windows 资源管理器」）。"

# 显式释放 COM，避免 .lnk 文件被短暂占用
[Runtime.InteropServices.Marshal]::ReleaseComObject($shell) | Out-Null
[GC]::Collect()
