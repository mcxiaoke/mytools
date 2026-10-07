<#
.SYNOPSIS
  编译 NoDelete（Windows / PowerShell 版）。

.DESCRIPTION
  脚本会自动把 MinGW-w64 的工具链目录加入当前进程的 PATH，因此
  不需要预先配置系统环境变量，也不会污染用户的全局环境。

  编译器按优先级探测：
    1. -Compiler 参数显式指定
    2. PATH 中已有的 gcc
    3. MSYS2  (ucrt64 -> mingw64 -> clang64)
    4. W64DevKit

  产出两个可执行文件：
    build\nodelete.exe      控制台版（带退出码与 --quiet，用于脚本/自动化）
    build\NoDeleteGUI.exe   图形版（-mwindows，无控制台窗口，用于「发送到」菜单）

.PARAMETER Compiler
  指定 gcc 可执行文件的完整路径，跳过自动探测。

.PARAMETER Clean
  存在时先删除 build 目录，做一次全新构建。

.PARAMETER Static
  存在时静态链接（默认开启；传 -NoStatic 可关闭）。静态链接可避免
  依赖 MinGW 运行时 DLL，从「发送到」菜单调用时更可靠。

.EXAMPLE
  pwsh -File build.ps1
  pwsh -File build.ps1 -Clean
  pwsh -File build.ps1 -Compiler C:\Home\Develop\msys64\ucrt64\bin\gcc.exe
#>
[CmdletBinding()]
param(
    [string]$Compiler,
    [switch]$Clean,
    [switch]$NoStatic
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$Root = $PSScriptRoot
$Src  = Join-Path $Root 'src\nodelete.c'
$Rc   = Join-Path $Root 'src\nodelete.rc'
$Out  = Join-Path $Root 'build'

# ---- 探测编译器 ------------------------------------------------------
# 记录原始 PATH，脚本结束时恢复，避免影响当前会话
$OriginalPath = $env:PATH

function Test-Gcc([string]$path) {
    if (-not $path) { return $false }
    if (-not (Test-Path -LiteralPath $path)) { return $false }
    # 必须是可执行文件而不是目录
    if (Test-Path -LiteralPath $path -PathType Container) { return $false }
    return $true
}

$candidates = New-Object System.Collections.Generic.List[string]
if ($Compiler) { $candidates.Add($Compiler) }

# 2) PATH 中已有的 gcc
$onPath = Get-Command gcc -ErrorAction SilentlyContinue
if ($onPath) { $candidates.Add($onPath.Source) }

# 3) MSYS2 的三个环境（ucrt64 优先，官方推荐的现代环境）
foreach ($env in @('ucrt64', 'mingw64', 'clang64')) {
    $candidates.Add("C:\Home\Develop\msys64\$env\bin\gcc.exe")
    $candidates.Add("C:\msys64\$env\bin\gcc.exe")
}

# 4) W64DevKit
$candidates.Add("C:\Home\Develop\w64devkit\bin\gcc.exe")

$Gcc = $null
foreach ($c in $candidates) {
    if (Test-Gcc $c) { $Gcc = (Resolve-Path -LiteralPath $c).Path; break }
}

if (-not $Gcc) {
    Write-Error @"
找不到 gcc。

已尝试的位置：
$(($candidates | ForEach-Object { "  - $_" }) -join "`n")

请任选一种方式解决：
  1) 安装 MSYS2 并执行 pacman -S mingw-w64-x86_64-gcc
     （之后本脚本会自动在 C:\Home\Develop\msys64 下查找）
  2) 用 -Compiler 参数显式指定 gcc 路径
  3) 把 MinGW 的 bin 目录加入系统 PATH
"@
    $env:PATH = $OriginalPath
    exit 1
}

$GccDir = Split-Path -Parent $Gcc

# gcc 需要能找到同目录下的 as / ld / cc1。
# 把工具链目录前置到 PATH 即可（仅影响当前进程）。
if ($env:PATH -notlike "*$GccDir*") {
    $env:PATH = "$GccDir;$OriginalPath"
}

try {
    Write-Host "使用编译器: $Gcc"
    (& $Gcc --version | Select-Object -First 1) | Write-Host

    if (-not (Test-Path -LiteralPath $Src)) {
        Write-Error "找不到源文件: $Src"
        exit 1
    }

    if ($Clean -and (Test-Path -LiteralPath $Out)) {
        Write-Host "==> 清理 build 目录"
        Remove-Item -LiteralPath $Out -Recurse -Force
    }
    if (-not (Test-Path -LiteralPath $Out)) {
        New-Item -ItemType Directory -Path $Out -Force | Out-Null
    }

    # 公共编译选项
    $common = @(
        '-O2'
        '-Wall', '-Wextra'
        '-DUNICODE', '-D_UNICODE'
        '-s'
    )
    if (-not $NoStatic) { $common += '-static' }

    $failed = $false

    # ---- 资源（图标 + 版本信息）--------------------------------------
    # windres 把 .rc 转成目标文件，再与 .c 一起链接，图标才会进 exe。
    # 图标只在 GUI 版上有意义（控制台版的图标基本看不到），故只给 GUI 版。
    $windres = Join-Path $GccDir 'windres.exe'
    $resObj  = $null
    if (Test-Path -LiteralPath $Rc) {
        if (-not (Test-Path -LiteralPath $windres)) {
            Write-Host "    警告：找不到 windres.exe，跳过图标/版本信息" -ForegroundColor Yellow
        } else {
            $resObj = Join-Path $Out 'nodelete_res.o'
            Write-Host "==> 编译资源 (图标 + 版本信息)"
            & $windres '-i' $Rc '-O' 'coff' '-o' $resObj
            if ($LASTEXITCODE -ne 0) {
                $failed = $true
                Write-Host "    失败 (exit $LASTEXITCODE)" -ForegroundColor Red
            }
        }
    }

    # ---- 控制台版（带退出码，供脚本调用）------------------------------
    # 源文件同时提供 wmain 与 WinMain；链接器只引用当前子系统所需的入口，
    # 因此两者可共存于同一源文件，分别用 -municode / -mwindows 编译。
    Write-Host "==> 编译 nodelete.exe (Console, -municode)"
    & $Gcc @common '-municode' '-o' (Join-Path $Out 'nodelete.exe') $Src '-lshell32'
    if ($LASTEXITCODE -ne 0) { $failed = $true; Write-Host "    失败 (exit $LASTEXITCODE)" -ForegroundColor Red }

    # ---- 图形版（无控制台窗口，弹 MessageBox 反馈）--------------------
    Write-Host "==> 编译 NoDeleteGUI.exe (GUI, -mwindows)"
    $guiArgs = @($common) + @('-mwindows', '-o', (Join-Path $Out 'NoDeleteGUI.exe'), $Src)
    if ($resObj) { $guiArgs += $resObj }   # 有资源时才链接
    $guiArgs += '-lshell32'
    & $Gcc @guiArgs
    if ($LASTEXITCODE -ne 0) { $failed = $true; Write-Host "    失败 (exit $LASTEXITCODE)" -ForegroundColor Red }

    if ($failed) { exit 1 }

    Write-Host ""
    Write-Host "构建完成：" -ForegroundColor Green
    Get-ChildItem -LiteralPath $Out -Filter '*.exe' |
        Format-Table Name, Length, LastWriteTime -AutoSize
}
finally {
    # 还原 PATH，保持调用者环境干净
    $env:PATH = $OriginalPath
}
