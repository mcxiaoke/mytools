#Requires -Version 5.1
<#
.SYNOPSIS
  saferm 的端到端（黑盒）测试：直接驱动构建出来的**可执行文件**。

.DESCRIPTION
  与 src/ 里的 Go 测试互补：Go 测试在进程内驱动 app，本脚本验证真正交付给用户的
  那个二进制在真实 shell 下的行为，重点是"护栏有没有真的拦住"以及
  "拒绝时原数据有没有被动过"。

  与 tests/run-e2e.sh 逐条对应，两边跑同一份检查清单。

  夹具位置很有讲究：
    - 普通用例放在**系统临时目录**（不在任何 Git 工作区内）。放在本仓库内部时，
      每个目标都会因为"位于版本库工作区内"被升为危险级（这是设计文档 §6.1 的
      预期行为，不是 bug），普通 `-y` 就推不动了。
    - 另有一条专门用例放在本仓库的 temp/ 下，反过来断言这个危险级升级确实生效。

  清理只针对本脚本自己刚创建的那个唯一命名的夹具目录，且做前缀校验，不用通配符。

.PARAMETER KeepFixture
  保留夹具目录，便于失败后人工查看。

.EXAMPLE
  ./tests/run-e2e.ps1
  ./tests/run-e2e.ps1 -KeepFixture
#>

[CmdletBinding()]
param(
    [switch]$KeepFixture
)

$ErrorActionPreference = "Stop"

# saferm 的日志与文案是 UTF-8（设计文档 §8.4）。Windows PowerShell 5.1 默认按旧代码页
# （中文机器上是 936）解码外部程序的输出，那样断言中文会全部"假失败"。
try {
    [Console]::OutputEncoding = New-Object System.Text.UTF8Encoding $false
} catch {
    # 没有控制台时设置会失败，跳过
}

$testsDir   = $PSScriptRoot
$projectDir = Split-Path -Parent $testsDir
$buildDir   = Join-Path $projectDir "build"

$isWindowsHost = ($env:OS -eq "Windows_NT")
$exe = if ($isWindowsHost) { Join-Path $buildDir "saferm.exe" } else { Join-Path $buildDir "saferm" }

# --- 没有二进制就先构建 ---
if (-not (Test-Path $exe)) {
    Write-Host "未找到 $exe，先执行 build.ps1 ..." -ForegroundColor Yellow
    & (Join-Path $projectDir "build.ps1") -NoSmoke
    if ($LASTEXITCODE -ne 0) { Write-Error "build.ps1 失败"; exit 1 }
}
if (-not (Test-Path $exe)) { Write-Error "构建后仍找不到 $exe"; exit 1 }

# --- 夹具 ---
$stamp     = Get-Date -Format "yyyyMMdd-HHmmss"
$tempBase  = [System.IO.Path]::GetTempPath().TrimEnd('\', '/')
$fixture   = Join-Path $tempBase ("saferm-e2e-ps-" + $stamp)          # 不在仓库里
$repoFix   = Join-Path $projectDir ("temp/e2e-inrepo-" + $stamp)      # 在仓库里

New-Item -ItemType Directory -Path $fixture -Force | Out-Null

$script:Pass = 0
$script:Fail = 0
$script:FailNames = @()

function Write-Section($text) {
    Write-Host ""
    Write-Host "--- $text ---" -ForegroundColor Cyan
}

function Assert-True {
    param([string]$Name, [bool]$Cond, [string]$Hint = "")
    if ($Cond) {
        $script:Pass++
        Write-Host ("  PASS  " + $Name) -ForegroundColor Green
    } else {
        $script:Fail++
        $script:FailNames += $Name
        Write-Host ("  FAIL  " + $Name) -ForegroundColor Red
        if ($Hint) {
            $shown = $Hint
            if ($shown.Length -gt 400) { $shown = $shown.Substring(0, 400) + " ..." }
            Write-Host ("        " + ($shown -replace "`r?`n", " | ")) -ForegroundColor DarkGray
        }
    }
}

# 跑一次 saferm，返回 @{ Code; Out }
#
# 一律通过管道喂 stdin（默认喂空）：这样无论本脚本怎么被启动，saferm 看到的
# stdin 都**不是终端**，即"非交互环境"，结果可复现——这也正是本工具要防的场景。
# 交互式确认路径（输入 yes、危险级手敲名字）需要真正的 PTY，由 Go 测试用注入覆盖。
#
# 参数名故意不叫 $Args：那是 PowerShell 的自动变量，同名会导致实参根本传不进去
# （踩过一次：所有调用都退化成"裸执行"，测试假通过）。
function Invoke-Saferm {
    param([string[]]$CmdArgs = @(), [string]$Stdin = "")
    $out = $Stdin | & $exe @CmdArgs 2>&1 | Out-String
    return @{ Code = $LASTEXITCODE; Out = $out }
}

function New-FixtureDir($name) {
    $p = Join-Path $fixture $name
    New-Item -ItemType Directory -Path $p -Force | Out-Null
    return $p
}

Write-Host "saferm E2E (PowerShell)" -ForegroundColor Cyan
Write-Host "  exe:     $exe"
Write-Host "  fixture: $fixture"
Write-Host "  in-repo: $repoFix"

# ===========================================================================
Write-Section "1. 版本与用法"
$r = Invoke-Saferm -CmdArgs @("--version")
Assert-True "版本输出且退出码 0" ($r.Code -eq 0 -and $r.Out -match "saferm") $r.Out

$r = Invoke-Saferm -CmdArgs @()
Assert-True "裸执行：退出码 1（绝不静默成功）" ($r.Code -eq 1) "code=$($r.Code) $($r.Out)"
Assert-True "裸执行：打印用法" ($r.Out -match "用法") $r.Out

$r = Invoke-Saferm -CmdArgs @("--definitely-not-a-flag")
Assert-True "未知开关：退出码 1" ($r.Code -eq 1) "code=$($r.Code) $($r.Out)"

# ===========================================================================
Write-Section "2. 空参数（本次事故的形态）"
$r = Invoke-Saferm -CmdArgs @("")
Assert-True "空串 operand：退出码 1" ($r.Code -eq 1) "code=$($r.Code) $($r.Out)"
Assert-True "空串 operand：提示单引号写法" ($r.Out -match "单引号" -or $r.Out -match '\$null') $r.Out
Assert-True "空串 operand：不是'没有给出任何路径'那条（说明确实收到了参数）" `
    ($r.Out -notmatch "没有给出任何路径") $r.Out

# ===========================================================================
Write-Section "3. 正常移动：原路径消失、镜像保留 tree、标记与清单齐全"
$work  = New-FixtureDir "happy/work"
$trash = Join-Path $fixture "happy/trash"
$target = Join-Path $work "a.txt"
Set-Content -Path $target -Value "hello safe rm" -NoNewline -Encoding utf8

$r = Invoke-Saferm -CmdArgs @("-y", "--trash-root", $trash, $target)
Assert-True "退出码 0" ($r.Code -eq 0) "code=$($r.Code) $($r.Out)"
Assert-True "原路径已消失" (-not (Test-Path $target)) $target
Assert-True "回收根已创建且带标记" (Test-Path (Join-Path $trash ".saferm-trash-root"))
$moved = Get-ChildItem -Path $trash -Recurse -File -Filter "a.txt" | Select-Object -First 1
Assert-True "文件出现在回收目录里" ($null -ne $moved)
if ($moved) {
    Assert-True "内容逐字节一致" ((Get-Content -Raw $moved.FullName) -eq "hello safe rm")
    $rel = $moved.FullName.Substring($trash.Length).TrimStart('\', '/')
    Assert-True "镜像保留了原目录结构（含 work/）" ($rel -match "work") $rel
}
Assert-True "清单文件与操作目录同级" ((Get-ChildItem -Path $trash -Filter "*.op.json" -File).Count -ge 1)
Assert-True "输出说明原数据未被永久删除" ($r.Out -match "永久删除") $r.Out
$opDir = Get-ChildItem -Path $trash -Directory | Select-Object -First 1
if ($opDir) {
    Assert-True "操作目录内没有清单等杂质文件" `
        ((Get-ChildItem -Path $opDir.FullName -Recurse -File -Filter "*.json").Count -eq 0)
}

# ===========================================================================
Write-Section "4. 路径不存在：默认报错，-f 才静默跳过"
$trash2 = Join-Path $fixture "missing/trash"
$r = Invoke-Saferm -CmdArgs @("-y", "--trash-root", $trash2, (Join-Path $fixture "nope.txt"))
Assert-True "不存在：退出码 1" ($r.Code -eq 1) "code=$($r.Code) $($r.Out)"

$r = Invoke-Saferm -CmdArgs @("-y", "-f", "--trash-root", $trash2, (Join-Path $fixture "nope.txt"))
Assert-True "-f：退出码 0" ($r.Code -eq 0) "code=$($r.Code) $($r.Out)"
Assert-True "-f：明确说明原数据未改动" ($r.Out -match "未改动" -or $r.Out -match "没有任何可移动") $r.Out
Assert-True "-f：不创建回收目录" (-not (Test-Path $trash2))

# ===========================================================================
Write-Section "5. 危险路径护栏（拒绝时原数据必须原封不动）"

# 5.1 目标就是当前工作目录
$cwdDir = New-FixtureDir "guard/cwd"
Push-Location $cwdDir
try {
    $r = Invoke-Saferm -CmdArgs @("-y", ".")
} finally {
    Pop-Location
}
Assert-True "目标是 cwd：退出码 1" ($r.Code -eq 1) "code=$($r.Code) $($r.Out)"
Assert-True "目标是 cwd：目录仍在" (Test-Path $cwdDir)

# 5.2 卷根
$volRoot = if ($isWindowsHost) { (Get-Item $fixture).PSDrive.Root } else { "/" }
$r = Invoke-Saferm -CmdArgs @("-y", $volRoot)
Assert-True "卷根 $volRoot：退出码 1" ($r.Code -eq 1) "code=$($r.Code) $($r.Out)"
Assert-True "卷根：拒绝理由写明是卷根" ($r.Out -match "卷根") $r.Out
Assert-True "卷根：卷根仍然存在" (Test-Path $volRoot)

# 5.3 版本库根（目录直接含 .git）——注意这与"在仓库内"是两回事
$repo = New-FixtureDir "guard/repo"
New-Item -ItemType Directory -Path (Join-Path $repo ".git") -Force | Out-Null
Set-Content -Path (Join-Path $repo "f.txt") -Value "x"
$r = Invoke-Saferm -CmdArgs @("-y", $repo)
Assert-True "版本库根：退出码 1" ($r.Code -eq 1) "code=$($r.Code) $($r.Out)"
Assert-True "版本库根：拒绝理由写明是版本库" ($r.Out -match "版本库") $r.Out
Assert-True "版本库根：目录纹丝不动" `
    ((Test-Path (Join-Path $repo "f.txt")) -and (Test-Path (Join-Path $repo ".git")))
Assert-True "版本库根：不创建任何回收目录" `
    ((Get-ChildItem -Path $fixture -Directory -Recurse -Filter ".saferm-trash*").Count -eq 0)

# ===========================================================================
Write-Section "6. 非交互环境的确认策略"
$dirTarget = New-FixtureDir "confirm/dir"
Set-Content -Path (Join-Path $dirTarget "f.txt") -Value "x"
$trash3 = Join-Path $fixture "confirm/trash"

$r = Invoke-Saferm -CmdArgs @("--trash-root", $trash3, $dirTarget)
Assert-True "非交互删目录：退出码 1" ($r.Code -eq 1) "code=$($r.Code) $($r.Out)"
Assert-True "非交互删目录：提示加 --yes/-f" ($r.Out -match "--yes") $r.Out
Assert-True "非交互删目录：目标原封不动" (Test-Path (Join-Path $dirTarget "f.txt"))
Assert-True "非交互删目录：不创建回收目录" (-not (Test-Path $trash3))

# ===========================================================================
Write-Section "7. 位于版本库工作区内 → 危险级（-y 不够，必须 --yes-i-am-sure）"
# 夹具放在本仓库的 temp/ 下：它本身没有 .git，但在仓库工作区之内。
$repoWork = Join-Path $repoFix "work"
New-Item -ItemType Directory -Path $repoWork -Force | Out-Null
$inRepoTarget = Join-Path $repoWork "r.txt"
Set-Content -Path $inRepoTarget -Value "in repo"
$trashRepo = Join-Path $repoFix "trash"

$r = Invoke-Saferm -CmdArgs @("-y", "--trash-root", $trashRepo, $inRepoTarget)
Assert-True "仓库内 + 仅 -y：退出码 1" ($r.Code -eq 1) "code=$($r.Code) $($r.Out)"
Assert-True "仓库内 + 仅 -y：要求 --yes-i-am-sure" ($r.Out -match "--yes-i-am-sure") $r.Out
Assert-True "仓库内 + 仅 -y：目标原封不动" (Test-Path $inRepoTarget)

$r = Invoke-Saferm -CmdArgs @("--yes-i-am-sure", "--trash-root", $trashRepo, $inRepoTarget)
Assert-True "仓库内 + --yes-i-am-sure：退出码 0" ($r.Code -eq 0) "code=$($r.Code) $($r.Out)"
Assert-True "仓库内 + --yes-i-am-sure：文件已移走" (-not (Test-Path $inRepoTarget))

# ===========================================================================
Write-Section "8. dry-run 必须零副作用"
$dryTarget = Join-Path (New-FixtureDir "dry/work") "d.txt"
Set-Content -Path $dryTarget -Value "dry"
$trash4 = Join-Path $fixture "dry/trash"
$r = Invoke-Saferm -CmdArgs @("-n", "--trash-root", $trash4, $dryTarget)
Assert-True "dry-run：退出码 0" ($r.Code -eq 0) "code=$($r.Code) $($r.Out)"
Assert-True "dry-run：输出里写明是 dry-run" ($r.Out -match "dry-run") $r.Out
Assert-True "dry-run：目标仍在" (Test-Path $dryTarget)
Assert-True "dry-run：不创建回收目录" (-not (Test-Path $trash4))

# ===========================================================================
Write-Section "9. 配置文件写错必须在动手之前硬失败；合法配置要真的生效"
$cfgDir = New-FixtureDir "config"
$badCfg = Join-Path $cfgDir "bad.toml"
Set-Content -Path $badCfg -Value "[trash]`ndefault_rooot = `"auto`"`n"
$cfgTarget = Join-Path (New-FixtureDir "config/work") "c.txt"
Set-Content -Path $cfgTarget -Value "still here" -NoNewline -Encoding utf8
$trash5 = Join-Path $fixture "config/trash"

$r = Invoke-Saferm -CmdArgs @("-y", "--config", $badCfg, "--trash-root", $trash5, $cfgTarget)
Assert-True "坏配置：退出码 1" ($r.Code -eq 1) "code=$($r.Code) $($r.Out)"
Assert-True "坏配置：报错带行号定位（如 2| ）" ($r.Out -match "\d+\|") $r.Out
Assert-True "坏配置：点出拼错的键" ($r.Out -match "default_rooot") $r.Out
Assert-True "坏配置：目标内容未变" ((Get-Content -Raw $cfgTarget) -eq "still here")
Assert-True "坏配置：不创建回收目录" (-not (Test-Path $trash5))

$goodCfg   = Join-Path $cfgDir "good.toml"
$cfgTrash  = Join-Path $fixture "config/good-trash"
$cfgValue  = $cfgTrash -replace '\\', '/'
Set-Content -Path $goodCfg -Value ("[trash]`ndefault_root = `"$cfgValue`"`n`n[confirm]`nfile_threshold = 1`nalways_confirm_dir = false`n")
$cfgTarget2 = Join-Path (New-FixtureDir "config/work2") "g.txt"
Set-Content -Path $cfgTarget2 -Value "moved by config"
$r = Invoke-Saferm -CmdArgs @("-y", "--config", $goodCfg, $cfgTarget2)
Assert-True "合法配置：退出码 0" ($r.Code -eq 0) "code=$($r.Code) $($r.Out)"
Assert-True "合法配置的 default_root 生效" (Test-Path (Join-Path $cfgTrash ".saferm-trash-root"))
Assert-True "合法配置：目标已移走" (-not (Test-Path $cfgTarget2))

# ===========================================================================
Write-Section "10. 两个长开关同现要打印显著警告"
$warnTarget = Join-Path (New-FixtureDir "warn/work") "w.txt"
Set-Content -Path $warnTarget -Value "w"
$r = Invoke-Saferm -CmdArgs @("--allow-dangerous", "--yes-i-am-sure", "--trash-root", (Join-Path $fixture "warn/trash"), $warnTarget)
Assert-True "双开关：打印警告且措辞到'不可逆'" ($r.Out -match "警告" -and $r.Out -match "不可逆") $r.Out

# ===========================================================================
Write-Section "11. where 只读且展示生效配置"
$before = (Get-ChildItem -Path $fixture -Directory -Recurse -Filter ".saferm-trash*").Count
$r = Invoke-Saferm -CmdArgs @("where")
Assert-True "where：退出码 0 或 1" ($r.Code -eq 0 -or $r.Code -eq 1) "code=$($r.Code)"
Assert-True "where：列出各卷回收目录" ($r.Out -match "各卷") $r.Out
Assert-True "where：展示生效配置" ($r.Out -match "生效配置") $r.Out
Assert-True "where：没有新建回收目录" `
    ((Get-ChildItem -Path $fixture -Directory -Recurse -Filter ".saferm-trash*").Count -eq $before)

# ===========================================================================
Write-Host ""
Write-Host "==========================================" -ForegroundColor Cyan
Write-Host ("通过 $script:Pass / 失败 $script:Fail") -ForegroundColor $(if ($script:Fail -eq 0) { "Green" } else { "Red" })

if (-not $KeepFixture) {
    # 只删本脚本自己刚建的那两个唯一命名目录，且先做前缀校验，不用通配符。
    foreach ($p in @($fixture, $repoFix)) {
        $isOurs = ($p -like "*saferm-e2e-ps-*") -or ($p -like "*e2e-inrepo-*")
        if ($isOurs -and (Test-Path $p)) {
            Remove-Item $p -Recurse -Force -ErrorAction SilentlyContinue
            Write-Host "已清理夹具：$p"
        }
    }
} else {
    Write-Host "保留夹具：$fixture"
    Write-Host "保留夹具：$repoFix"
}

if ($script:Fail -gt 0) {
    Write-Host "失败的用例：" -ForegroundColor Red
    $script:FailNames | ForEach-Object { Write-Host ("  - " + $_) -ForegroundColor Red }
    exit 1
}
Write-Host "全部通过。" -ForegroundColor Green
exit 0
