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

# 形态守卫（PROPOSAL-pack-subcommand.md D1）：发布产物**不得**包含 packer 的写侧。
# 为什么需要：写 zip 的代码在 release profile 下约 +54 KB（同 crate 只读 218112 B vs 读+写 271872 B），
# 而打包是**构建期**能力，没有理由让每个最终用户承担。
# 判据用 packer CLI 独有的参数字面量：updater.exe 的 CLI 面根本不存在这两个开关，
# 一旦它们出现在产物里，说明写侧被链了进来（例如有人把 pack 加进 default features 并从主链调用）。
$bytes = [IO.File]::ReadAllBytes($exe)
$ascii = [Text.Encoding]::ASCII.GetString($bytes)
foreach ($marker in @('--min-upgradable-from', '--app-version')) {
    if ($ascii.Contains($marker)) {
        throw "发布产物内出现 packer 独有字面量 '$marker' —— 写侧被链进了 updater.exe（体积与攻击面双重放大）。检查 Cargo.toml 的 default features 与调用点。"
    }
}
$packer = Join-Path $root "target\release\packer.exe"
$packerNote = if (Test-Path $packer) {
    "$([math]::Round((Get-Item $packer).Length / 1KB, 1)) KB（开发期工具，不计入发布预算）"
} else {
    "(未构建；cargo build --release --features pack --bin packer)"
}
Write-Host "[OK] 形态守卫：发布产物不含 packer 写侧（packer.exe $packerNote）"

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
packer_exe = $packerNote
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
