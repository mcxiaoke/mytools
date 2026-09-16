# 构建两个产物（PLAN.md §3.2 / TESTING.md §2）
#
# 用法:
#   pwsh -File scripts/build.ps1                       # 产出到 target/dist/
#   pwsh -File scripts/build.ps1 -Out <目录>            # 自定义输出目录
#   pwsh -File scripts/build.ps1 -NoCopy               # 只构建，不复制
#
# 产出:
#   <Out>\updater.exe   更新器本体（发布产物）
#   <Out>\packer.exe    打包工具（开发期工具，不随更新器发布）
#
# 退出码: 0 = 两个产物就绪；非 0 = 构建失败（就地停下）
#
# **一次构建即可产出两个产物**：`cargo build --release --features pack` 会构建本 package 的
# 全部 bin。本脚本存在的价值只是把输出**收集**到一处并打印大小与 sha256，不做别的。
#
# 为什么不需要"先用默认 feature 再构一遍 updater"（2026-09-16 实测后修正）:
#   ① 带 --features pack 构建出的 updater.exe 与默认 feature **大小完全相同**（479744 B）：
#      写侧只被 packer.exe 引用，未被引用的代码在 lto + codegen-units=1 + strip 下被丢弃；
#   ② 本项目的 release 构建**本身不可复现**（内容与 feature 都不变、强制重编，sha256 仍然不同），
#      所以"字节相同"从来不能作为判据，唯一稳定的量是大小。
#   ③ 真正兜住"写侧不得进入 updater.exe"的是 scripts/check_size.ps1 的**形态守卫**
#      （断言产物内不存在 packer 独有字面量），不是构建顺序。
#   因此脚本里只有**一条** cargo 命令；旧版分两次构建属于无效仪式，已删除。
#
# 本脚本只负责**产出**；正确性与守卫由 scripts/verify.ps1（全量机械守卫，内部复用本脚本）负责。
param(
    [string]$Out = "",
    [switch]$NoCopy
)

$ErrorActionPreference = "Stop"
$root = Split-Path -Parent $PSScriptRoot
Set-Location $root

if (-not $Out) { $Out = Join-Path $root "target\dist" }

Write-Host "=== cargo build --release --features pack ==="
& cargo build --release --features pack
if ($LASTEXITCODE -ne 0) { throw "构建失败（exit $LASTEXITCODE）" }

$updater = Join-Path $root "target\release\updater.exe"
$packer = Join-Path $root "target\release\packer.exe"
foreach ($p in @($updater, $packer)) {
    if (-not (Test-Path $p)) { throw "缺少产物: $p" }
}

if (-not $NoCopy) {
    New-Item -ItemType Directory -Force -Path $Out | Out-Null
    Copy-Item $updater (Join-Path $Out "updater.exe") -Force
    Copy-Item $packer (Join-Path $Out "packer.exe") -Force
}

Write-Host ""
Write-Host "=== 产物 ==="
foreach ($name in @("updater.exe", "packer.exe")) {
    $p = if ($NoCopy) { Join-Path $root "target\release\$name" } else { Join-Path $Out $name }
    $kb = [math]::Round((Get-Item $p).Length / 1KB, 1)
    $h = (Get-FileHash $p -Algorithm SHA256).Hash.ToLower()
    Write-Host ("  {0,-12} {1,8} KB  sha256 {2}" -f $name, $kb, $h)
    Write-Host ("  {0,-12} {1}" -f "", $p)
}

Write-Host ""
Write-Host "提示: packer 是**开发期**工具（不随 updater 发布）。打发布包用:"
Write-Host "      packer --stage <stage 目录> --version <版本> --out <zip>"
Write-Host "      全量机械守卫请跑 pwsh -File scripts/verify.ps1（本脚本不含守卫）"
exit 0
