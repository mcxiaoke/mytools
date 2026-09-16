# 杀软/EDR 干扰验证（RELEASE-READINESS P1-2）
#
# 目的：本工具在每次更新时都会**把自身复制到 %LOCALAPPDATA%\...\runtime\ 并 detached 执行**
#       —— 这是 EDR 的经典启发式特征（"进程自我复制到用户目录后执行"）。
#       本脚本在**真实实时防护开启**的机器上跑一次"最可疑形态"的完整更新，并归档证据。
#
# "最可疑形态" = 方案 A：updater.exe 位于安装目录内
#       ⇒ 触发影子 Worker（自我复制 + detached 派生）⇒ 由 Worker 拉起看门狗（再复制一次）
#       ⇒ Worker 还负责把 target\updater.exe 自身替换成新版本（自更新）
#
# 用法:
#   powershell -ExecutionPolicy Bypass -File scripts\check_av.ps1 [-RequireActiveAv]
#   -RequireActiveAv : 未检测到任何实时防护时直接失败（CI 用，保证这一步不是空跑）
#
# 退出码: 0 = 通过或不阻断；非 0 = 更新在实时防护下**功能失败**（属于发布阻断问题）
param(
    [switch]$RequireActiveAv
)

$ErrorActionPreference = "Stop"
$root = Split-Path -Parent $PSScriptRoot
$art = Join-Path $root "docs\artifacts"
New-Item -ItemType Directory -Force -Path $art | Out-Null
$report = Join-Path $art "av-scan.txt"

function Write-Utf8NoBom([string]$Path, [string[]]$Lines) {
    $enc = New-Object System.Text.UTF8Encoding($false)
    $text = (($Lines -join "`n").TrimEnd("`r", "`n")) + "`n"
    [System.IO.File]::WriteAllText($Path, $text, $enc)
}

# 报告累积器：用带前缀的显式 script 作用域名。
# ⚠️ 不要用单字母名（如 $L）—— PowerShell 变量名大小写不敏感，
#    任何形如 foreach ($l in ...) 的循环变量都会把它覆盖掉。
$script:ReportLines = New-Object System.Collections.Generic.List[string]
function Say([string]$s) { Write-Host $s; $script:ReportLines.Add($s) }

Say "# 杀软/EDR 干扰验证记录（由 scripts/check_av.ps1 生成）"
Say "# 生成时间: $(Get-Date -Format 'yyyy-MM-dd HH:mm:ss zzz')"
Say "# 形态: 方案 A（updater.exe 位于安装目录内）—— 影子 Worker + 自更新 + 看门狗，最可疑形态"
Say ""

# ---------------------------------------------------------------- 1. 检测实时防护
Say "## 1. 实时防护检测"
$avFound = @()
$hrQuar = Join-Path $env:ProgramData "Huorong\Sysdiag\Quarantine"
foreach ($p in @("HipsDaemon", "HipsTray", "wsctrlsvc")) {
    $proc = Get-Process -Name $p -ErrorAction SilentlyContinue
    if ($proc) { $avFound += "火绒 $p (PID $($proc[0].Id))" }
}
$hrVer = ""
$hrVerFile = Join-Path ${env:ProgramFiles} "Huorong\Sysdiag\VERSION"
if (Test-Path $hrVerFile) { $hrVer = (Get-Content $hrVerFile -Raw).Trim() }

$defenderOn = $false
try {
    $st = Get-MpComputerStatus -ErrorAction Stop
    $defenderOn = [bool]$st.AMServiceEnabled -and [bool]$st.AntivirusEnabled
    if ($defenderOn) { $avFound += "Microsoft Defender (AMServiceEnabled=True)" }
} catch { }

if ($avFound.Count -eq 0) {
    Say "状态: NO_ACTIVE_AV —— 未检测到实时防护（本步骤退化为空跑）"
    if ($RequireActiveAv) { throw "未检测到任何实时防护，但传入了 -RequireActiveAv" }
} else {
    foreach ($a in $avFound) { Say "  - $a" }
}
if ($hrVer) { Say "  火绒版本: $hrVer" }
Say ""

# 基线：隔离区
$quarBefore = 0
if (Test-Path $hrQuar) { $quarBefore = @(Get-ChildItem $hrQuar -Force -ErrorAction SilentlyContinue).Count }
$defThreatsBefore = 0
try { $defThreatsBefore = @(Get-MpThreatDetection -ErrorAction SilentlyContinue).Count } catch { }
Say "  基线隔离区条目: 火绒=$quarBefore  Defender威胁记录=$defThreatsBefore"
Say ""

# ---------------------------------------------------------------- 2. 构造"方案 A"现场
Say "## 2. 构造现场（updater.exe 在安装目录内）"
$exe = Join-Path $root "target\release\updater.exe"
if (-not (Test-Path $exe)) { throw "找不到 $exe —— 请先执行 cargo build --release" }

$sandbox = Join-Path $env:TEMP ("bu-av-" + (Get-Date -Format 'HHmmss'))
$target = Join-Path $sandbox "app"
$stage = Join-Path $sandbox "stage"
New-Item -ItemType Directory -Force -Path $target, $stage | Out-Null

# 旧版应用（真实 PE，保证可拉起） + 用户数据
Copy-Item "$env:SystemRoot\System32\cmd.exe" (Join-Path $target "app.exe")
Set-Content -Path (Join-Path $target "version.txt") -Value "1.0.0" -NoNewline
Set-Content -Path (Join-Path $target ".updatekeep") -Value "# 保护用户数据`nuserdata\" -NoNewline
New-Item -ItemType Directory -Force -Path (Join-Path $target "userdata") | Out-Null
Set-Content -Path (Join-Path $target "userdata\save.dat") -Value "user save data" -NoNewline
# ★ 关键：updater.exe 放进安装目录 ⇒ 触发影子 Worker
Copy-Item $exe (Join-Path $target "updater.exe")

# 新版内容（含 updater.exe 自身，走自更新）
# ★ stage 里的 updater.exe 必须与 target 里的**字节不同**，否则哈希比对无法证明"自更新真的发生了"。
#   这里用 debug 构建的 updater.exe 作为"新版 updater"，它同样是合法未改动的 PE。
$exeNew = Join-Path $root "target\debug\updater.exe"
if (-not (Test-Path $exeNew)) {
    Push-Location $root
    try { & cargo build --quiet 2>&1 | Out-Null } finally { Pop-Location }
}
if (-not (Test-Path $exeNew)) { throw "找不到 debug 构建 $exeNew —— 请先 cargo build" }
if ((Get-FileHash $exe).Hash -eq (Get-FileHash $exeNew).Hash) {
    throw "release 与 debug 构建字节相同，无法证明自更新；请先在 debug 与 release 间做一次改动"
}

Copy-Item $exe (Join-Path $stage "app.exe")
Set-Content -Path (Join-Path $stage "version.txt") -Value "1.1.0" -NoNewline
New-Item -ItemType Directory -Force -Path (Join-Path $stage "plugins") | Out-Null
Set-Content -Path (Join-Path $stage "plugins\p.dll") -Value "plugin payload" -NoNewline
Copy-Item $exeNew (Join-Path $stage "updater.exe")

$zip = Join-Path $sandbox "update.zip"
& (Join-Path $PSScriptRoot "release_pack.ps1") -Stage $stage -Version "1.1.0" -Out $zip | Out-Null
$packRc = $LASTEXITCODE
Say "  打包: rc=$packRc"
if ($packRc -ne 0) { throw "release_pack.ps1 失败" }

# ---------------------------------------------------------------- 3. 运行（真实防护下）
Say ""
Say "## 3. 在实时防护下执行更新"
$ulog = Join-Path $sandbox "updater.log"
# 注意：用 target 内的 updater 启动 ⇒ 主实例只做交接并立即退出（这是设计契约，不是失败）
$p = Start-Process -FilePath (Join-Path $target "updater.exe") `
    -ArgumentList @("--target", $target, "--zip", $zip, "--launch", "app.exe", "--log", $ulog, "--delete-zip") `
    -Wait -PassThru -NoNewWindow
Say "  主实例退出码 = $($p.ExitCode)（0 只表示「已交接/已提交」，不承载最终结果）"

# 等收尾真正完成：journal 与 lock 都消失
$done = $false
for ($i = 0; $i -lt 120; $i++) {
    if (-not (Test-Path (Join-Path $target ".updater\journal")) -and -not (Test-Path (Join-Path $target ".updater\lock"))) {
        $done = $true; break
    }
    Start-Sleep -Milliseconds 500
}
Say "  收尾完成（journal 与 lock 均已消失）= $done"
Say ""

# ---------------------------------------------------------------- 4. 功能断言
Say "## 4. 功能断言（实时防护不得干扰任何一步）"
$logText = if (Test-Path $ulog) { Get-Content $ulog -Raw } else { "" }
$checks = [ordered]@{
    "更新已收尾（无残留 journal/lock）" = $done
    "新版 app.exe 就位"                 = ((Get-Content (Join-Path $target "version.txt") -Raw) -eq "1.1.0")
    "新增文件 plugins\p.dll 已落盘"     = (Test-Path (Join-Path $target "plugins\p.dll"))
    "updater.exe 自身已更新（自更新）"  = ((Get-FileHash (Join-Path $target "updater.exe")).Hash -eq (Get-FileHash $exeNew).Hash)
    "用户数据未被覆盖"                  = ((Get-Content (Join-Path $target "userdata\save.dat") -Raw) -eq "user save data")
    "更新包已被 --delete-zip 删除"      = (-not (Test-Path $zip))
    "日志出现影子 Worker 交接"          = ($logText -match "shadow worker spawned")
    "日志出现看门狗派生"                = ($logText -match "watchdog spawned")
    "日志出现提交成功"                  = ($logText -match "END: COMMITTED")
}
$fail = @()
foreach ($k in $checks.Keys) {
    $ok = [bool]$checks[$k]
    Say ("  [{0}] {1}" -f $(if ($ok) { "PASS" } else { "FAIL" }), $k)
    if (-not $ok) { $fail += $k }
}
Say ""

# ---------------------------------------------------------------- 5. 事后取证
Say "## 5. 事后取证（隔离区不得新增）"
$quarAfter = 0
if (Test-Path $hrQuar) { $quarAfter = @(Get-ChildItem $hrQuar -Force -ErrorAction SilentlyContinue).Count }
$defThreatsAfter = 0
try { $defThreatsAfter = @(Get-MpThreatDetection -ErrorAction SilentlyContinue).Count } catch { }
Say "  火绒隔离区: $quarBefore -> $quarAfter"
Say "  Defender威胁记录: $defThreatsBefore -> $defThreatsAfter"
if ($quarAfter -gt $quarBefore) {
    Say "  [FAIL] 火绒隔离区新增条目："
    Get-ChildItem $hrQuar -Force | Select-Object -Last 5 | ForEach-Object { Say ("    - " + $_.Name) }
    $fail += "火绒隔离区新增条目"
}
if ($defThreatsAfter -gt $defThreatsBefore) { $fail += "Defender 新增威胁记录" }

# 运行期副本是否真的落盘并被启动（自我复制行为的客观证据）
$rtRoot = Join-Path $env:LOCALAPPDATA "app-updater"
Say "  运行期目录: $rtRoot"
if (Test-Path $rtRoot) {
    Get-ChildItem (Join-Path $rtRoot "runtime") -Force -ErrorAction SilentlyContinue |
        Select-Object -First 6 | ForEach-Object { Say ("    - " + $_.Name) }
}
Say ""

# ---------------------------------------------------------------- 6. 结论
Say "## 6. 结论"
if ($avFound.Count -eq 0) {
    Say "状态: NO_ACTIVE_AV —— 本次**不能**证明实时防护下可用，请在有实时防护的机器上重跑。"
} elseif ($fail.Count -gt 0) {
    Say "状态: FAIL —— 在实时防护下功能异常，失败项：$($fail -join '；')"
    Say "这是**发布阻断**问题：更新器在真实防护环境下不可用。"
} else {
    Say "状态: PASS —— 实时防护开启下，影子 Worker + 自更新 + 看门狗全链路均正常，隔离区零新增。"
}
Say ""
Say "## 附：更新器日志原文"
Say '```'
if ($logText) { foreach ($line in ($logText -split "`n")) { Say $line.TrimEnd() } } else { Say "(无日志)" }
Say '```'

Write-Utf8NoBom $report $script:ReportLines.ToArray()

# 清理现场（不删运行期目录 —— 那是产品行为留下的，留给下次启动自清理）
Remove-Item $sandbox -Recurse -Force -ErrorAction SilentlyContinue

if ($avFound.Count -gt 0 -and $fail.Count -gt 0) {
    throw "实时防护下功能验证失败：$($fail -join '；')（详见 $report）"
}
Write-Host "已归档: $report"
exit 0
