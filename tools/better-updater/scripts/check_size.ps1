# 体积守卫 + 快照归档（TESTING.md §2.1）
# 用法: powershell -ExecutionPolicy Bypass -File scripts\check_size.ps1
#
# 上界告警、下界异常检测（下界命中通常意味着 feature 被误裁或构建未包含功能）。
# 同时把产物大小与依赖快照写入 docs/artifacts/，供体积回归时定位放大来源。
$ErrorActionPreference = "Stop"
$root = Split-Path -Parent $PSScriptRoot

function Write-Utf8NoBom([string]$Path, [string[]]$Lines) {
    $enc = New-Object System.Text.UTF8Encoding($false)
    $text = (($Lines -join "`n").TrimEnd("`r", "`n")) + "`n"
    [System.IO.File]::WriteAllText($Path, $text, $enc)
}

$exe = Join-Path $root "target\release\updater.exe"
if (-not (Test-Path $exe)) {
    throw "未找到 $exe —— 请先运行 cargo build --release"
}

$size = (Get-Item $exe).Length
$kb = [math]::Round($size / 1KB, 1)
Write-Host "[INFO] updater.exe = $kb KB ($size bytes)"
if ($size -gt 750KB) { throw "体积超上限: $kb KB（检查是否误开 zip 的 deflate/zopfli）" }
if ($size -lt 250KB) { throw "体积低于下限: $kb KB（检查 feature 是否被误裁）" }

$art = Join-Path $root "docs\artifacts"
New-Item -ItemType Directory -Force -Path $art | Out-Null

$stamp = Get-Date -Format 'yyyy-MM-dd HH:mm:ss zzz'
Write-Utf8NoBom (Join-Path $art "size.txt") @"
# updater.exe 体积快照（由 scripts/check_size.ps1 生成）
# 预算区间：250 KB（下界）~ 750 KB（上界）；目标值 ~500 KB（PLAN.md §2.2）

size_bytes = $size
size_kb    = $kb
threshold_min_kb = 250
threshold_max_kb = 750
measured_at = $stamp
"@

$meta = cargo metadata --format-version 1 --no-deps --manifest-path (Join-Path $root "Cargo.toml")
Write-Utf8NoBom (Join-Path $art "deps.json") $meta

# 直接依赖数（机械口径，不用 cargo tree 的树形字符）
$obj = $meta | ConvertFrom-Json
$deps = @($obj.packages[0].dependencies | Where-Object { $_.kind -ne 'dev' })
$names = ($deps | ForEach-Object { $_.name }) -join ', '
Write-Host "[INFO] 直接依赖数: $($deps.Count) ($names)"
if ($deps.Count -gt 7) { throw "直接依赖超预算: $($deps.Count)（上限 7）" }

Write-Host "[OK] 体积守卫通过；size.txt 与 deps.json 已归档到 docs/artifacts/"
