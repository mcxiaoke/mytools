# 分层守卫脚本（TESTING.md §2.3 重写版 / PLAN.md §3.1）
# 用法: powershell -ExecutionPolicy Bypass -File scripts\check_tiers.ps1
# 检查项：
#   1) 每个 .rs 第一行必须声明 `// Tier: 0|1|2`
#   2) Tier 0 产品代码行数（不含 #[cfg(test)] 之后）：>1200 WARN（首发止损线，需架构评审）；>2600 WARN（终态）
#   3) Tier 0 禁用构造（剥离注释与字符串字面量后匹配）：std::thread / async fn / tokio / Box<dyn / dyn T —— 硬失败
#   4) 直接依赖数量（cargo metadata）：>7 硬失败
$ErrorActionPreference = "Stop"
$root = Split-Path -Parent $PSScriptRoot
$srcFiles = Get-ChildItem (Join-Path $root "src") -Recurse -Filter *.rs

$undeclared = @()
$tier0ProdLines = 0
$forbidden = '\bstd::thread\b|\basync\s+fn\b|\btokio\b|\bBox<dyn\b|\bdyn\s+[A-Za-z_]'
$violations = @()

foreach ($f in $srcFiles) {
    $lines = Get-Content $f.FullName -Encoding UTF8
    if ($lines.Count -eq 0 -or ($lines[0] -notmatch '//\s*Tier:\s*[012]')) {
        $undeclared += $f.Name
        continue
    }
    if ($lines[0] -match '//\s*Tier:\s*0') {
        # 产品代码行数：#[cfg(test)] 之前
        $cut = 0
        for ($i = 0; $i -lt $lines.Count; $i++) {
            if ($lines[$i] -match '^\s*#\[cfg\(test\)\]') { $cut = $i; break }
        }
        if ($cut -gt 0) { $tier0ProdLines += $cut } else { $tier0ProdLines += $lines.Count }
        # 禁用构造：先剥离注释与字符串字面量
        $code = $lines | ForEach-Object { ($_ -replace '//.*$', '') -replace '"(?:[^"\\]|\\.)*"', '""' }
        $hits = $code | Select-String -Pattern $forbidden
        if ($hits) {
            $violations += "$($f.Name): $($hits[0].Line.Trim())"
        }
    }
}

if ($undeclared.Count -gt 0) {
    throw "存在未声明 Tier 的模块: $($undeclared -join ', ')"
}
Write-Host "[OK] 全部 $($srcFiles.Count) 个模块已声明 Tier"

if ($violations.Count -gt 0) {
    throw "Tier 0 出现禁用构造: $($violations -join '; ')"
}
Write-Host "[OK] Tier 0 无并发/间接层禁用构造"

$meta = cargo metadata --format-version 1 --no-deps --manifest-path (Join-Path $root "Cargo.toml") | ConvertFrom-Json
$deps = @($meta.packages[0].dependencies | Where-Object { $_.kind -ne 'dev' })
Write-Host "[INFO] 直接依赖数: $($deps.Count)（上限 7 = windows-sys + 6 第三方）"
if ($deps.Count -gt 7) {
    throw "直接依赖超预算: $($deps.Count)"
}

Write-Host "[INFO] Tier 0 产品代码: $tier0ProdLines 行（首发止损线 1200，终态上限 2600）"
if ($tier0ProdLines -gt 2600) {
    Write-Warning "Tier 0 产品代码 $tier0ProdLines 行 > 2600：触发架构评审"
} elseif ($tier0ProdLines -gt 1200) {
    Write-Warning "Tier 0 产品代码 $tier0ProdLines 行 > 1200（首发止损线）：需就地评审与记录"
} else {
    Write-Host "[OK] Tier 0 在首发预算内"
}

Write-Host "分层守卫全部通过"
