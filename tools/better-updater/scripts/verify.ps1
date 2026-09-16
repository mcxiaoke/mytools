# 全量机械守卫 —— CI 与本地的唯一入口（TESTING.md §2）
#
# 用法: powershell -ExecutionPolicy Bypass -File scripts\verify.ps1
# 退出码: 0 = 全部通过；非 0 = 第一处失败（就地停下，不做后续步骤）
#
# 步骤（与 TESTING.md §2.2 逐条对应）：
#   1) cargo build --release --no-default-features —— RM 缩回开关可用
#   2) cargo build --release                 —— feature 完整性验证（**必须是最后落盘的产物**）
#   3) cargo test --all-targets              —— 单元 + e2e + 不变量 + 能力（结果归档）
#   4) cargo clippy --all-targets -D warnings —— 0 警告
#   5) scripts\check_tiers.ps1               —— 分层守卫（Tier 声明 / 禁用构造 / 依赖数）
#   6) scripts\check_size.ps1                —— 体积守卫 + 产物快照归档
#   7) Defender 扫描归档（docs/artifacts/defender-scan.txt）—— 可用则执行
$ErrorActionPreference = "Stop"
$root = Split-Path -Parent $PSScriptRoot
Set-Location $root

$art = Join-Path $root "docs\artifacts"
New-Item -ItemType Directory -Force -Path $art | Out-Null

function Write-Utf8NoBom([string]$Path, [string[]]$Lines) {
    # 显式按行拼接：直接把数组传给 .NET 会被空格连接，丢掉换行
    $enc = New-Object System.Text.UTF8Encoding($false)
    $text = (($Lines -join "`n").TrimEnd("`r", "`n")) + "`n"
    [System.IO.File]::WriteAllText($Path, $text, $enc)
}

function Invoke-Step([string]$Name, [scriptblock]$Body) {
    Write-Host ""
    Write-Host "=== $Name ==="
    $out = & $Body 2>&1
    $out | ForEach-Object { Write-Host $_ }
    if ($LASTEXITCODE -ne 0) {
        throw "$Name 失败（exit $LASTEXITCODE）"
    }
    return $out
}

# 1) 缩回开关：RM 诊断关闭后仍须可构建（其余行为不变）
Invoke-Step "cargo build --release --no-default-features" { cargo build --release --no-default-features } | Out-Null

# 2) 完整 feature 构建 —— 必须最后落盘，体积守卫测的就是这个产物
Invoke-Step "cargo build --release" { cargo build --release } | Out-Null

# 3) 测试（原文归档，便于回溯失败明细）
$testOut = Invoke-Step "cargo test --all-targets" { cargo test --all-targets }
Write-Utf8NoBom (Join-Path $art "test-summary.txt") (@(
    "# cargo test --all-targets 结果（由 scripts/verify.ps1 生成）",
    "# 生成时间: $(Get-Date -Format 'yyyy-MM-dd HH:mm:ss zzz')",
    ""
) + $testOut)

$summary = $testOut | Select-String -Pattern '^test result:' | ForEach-Object { $_.Line }
Write-Host ""
$summary | ForEach-Object { Write-Host "  $_" }
$failed = @($summary | Where-Object { $_ -notmatch '0 failed' })
if ($failed.Count -gt 0) { throw "存在失败用例：$($failed -join ' | ')" }

# 4) clippy（0 警告）
Invoke-Step "cargo clippy --all-targets" { cargo clippy --all-targets -- -D warnings } | Out-Null

# 5) 分层守卫
Invoke-Step "scripts/check_tiers.ps1" { & (Join-Path $PSScriptRoot "check_tiers.ps1") } | Out-Null

# 6) 体积守卫 + 快照
Invoke-Step "scripts/check_size.ps1" { & (Join-Path $PSScriptRoot "check_size.ps1") } | Out-Null

# 7) Defender 扫描（仅归档误报状态；**绝不阻断**——误报是"发布检查单"要记录的事实）
Write-Host ""
Write-Host "=== Defender 扫描 ==="
$mp = $null
$platRoot = Join-Path $env:ProgramData "Microsoft\Windows Defender\Platform"
if (Test-Path $platRoot) {
    $mp = Get-ChildItem $platRoot -Directory -ErrorAction SilentlyContinue |
        Sort-Object Name -Descending |
        ForEach-Object { Join-Path $_.FullName "MpCmdRun.exe" } |
        Where-Object { Test-Path $_ } |
        Select-Object -First 1
}
$exe = Join-Path $root "target\release\updater.exe"
$header = @(
    "# Defender 扫描记录（由 scripts/verify.ps1 生成）",
    "# 生成时间: $(Get-Date -Format 'yyyy-MM-dd HH:mm:ss zzz')",
    "# 目标: $exe",
    "# 说明: 本步骤只归档误报状态，不阻断构建（DoD 第 6 条的归档要求）。",
    ""
)
if ($mp) {
    # 先判别 AM 服务是否在运行：未运行时 MpCmdRun 会以 0x80004005 失败，这属于**环境限制**
    # 而非构建问题，必须与"扫描通过"区分开记录。
    $amOn = $false
    try {
        $st = Get-MpComputerStatus -ErrorAction Stop
        $amOn = [bool]$st.AMServiceEnabled -and [bool]$st.AntivirusEnabled
    } catch {
        $amOn = $false
    }
    if (-not $amOn) {
        Write-Utf8NoBom (Join-Path $art "defender-scan.txt") (@($header) + @(
            "# 状态: UNAVAILABLE —— Defender AM 服务未运行（AMServiceEnabled=False）",
            "# 这不是构建问题：本机没有可用的实时防护引擎，MpCmdRun 无法执行扫描。",
            "# 发布检查单：在有 Defender 的机器上补做一次扫描，并把结果覆盖到本文件。",
            "# 参考：UPX 已明确禁用（PLAN.md §2.1），产物为单文件无壳 PE。"
        ))
        Write-Host "[WARN] Defender AM 服务未运行 —— 已记为 UNAVAILABLE（环境限制，不阻断）"
    } else {
        # 先尝试带 -DisableRemediation（只报告不处置，避免守卫自己把产物隔离掉）；
        # 若该组合在本机不受支持，退回基础扫描。
        $scan = @(& $mp -Scan -ScanType 3 -File $exe -DisableRemediation 2>&1)
        $joined = $scan -join "`n"
        if ($joined -match 'Failed with hr') {
            Write-Host "  [INFO] -DisableRemediation 组合不受支持，退回基础扫描"
            $scan = @(& $mp -Scan -ScanType 3 -File $exe 2>&1)
            $joined = $scan -join "`n"
        }
        if ($joined -match 'Failed with hr') {
            Write-Utf8NoBom (Join-Path $art "defender-scan.txt") (@($header) + @(
                "# 状态: UNAVAILABLE —— AM 服务在运行但扫描调用失败（详见下方原始输出）",
                "# 发布检查单：排查 MpCmdRun 调用参数或权限，并补做扫描归档。",
                ""
            ) + $scan)
            Write-Host "[WARN] 扫描不可用，已记为 UNAVAILABLE（不阻断）"
        } else {
            Write-Utf8NoBom (Join-Path $art "defender-scan.txt") (@($header) + @(
                "# 状态: SCANNED —— 无威胁即为预期结果（UPX 已明确禁用，见 PLAN.md §2.1）",
                ""
            ) + $scan)
            Write-Host "[OK] 扫描结果已归档到 docs/artifacts/defender-scan.txt"
        }
        $scan | ForEach-Object { Write-Host "  $_" }
    }
} else {
    Write-Utf8NoBom (Join-Path $art "defender-scan.txt") (@($header) + @(
        "# 状态: SKIPPED —— 未找到 MpCmdRun.exe（未安装 Defender 或平台目录不可访问）",
        "# 发布检查单：在有 Defender 的机器上补做一次扫描并归档误报状态。"
    ))
    Write-Host "[WARN] 未找到 MpCmdRun.exe，已记为 SKIPPED（不阻断）"
}

Write-Host ""
Write-Host "=== 杀软/EDR 干扰验证 ==="
# 用"最可疑形态"（updater 在安装目录内 ⇒ 影子 Worker 自我复制 + detached 派生 + 自更新）
# 跑一次真实更新。未检测到实时防护时退化为 NO_ACTIVE_AV 空跑，不阻断；
# 一旦检测到实时防护而功能失败，则**阻断**——更新器在真实防护环境下不可用属于发布阻断问题。
Invoke-Step "scripts/check_av.ps1" { & (Join-Path $PSScriptRoot "check_av.ps1") } | Out-Null

Write-Host ""
Write-Host "全部守卫通过。产物快照见 docs/artifacts/"
# 复位退出码：上面可能有"不阻断"的原生调用失败（如 Defender），不得污染本脚本的退出状态
exit 0

