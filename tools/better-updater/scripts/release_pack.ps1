# 发布打包流水线（RELEASE-READINESS P2-1）
#
# 把"组装目录 → 生成清单 → 压缩 → 校验 → 输出待上传产物"串成一条命令。
#
# 用法:
#   powershell -ExecutionPolicy Bypass -File scripts\release_pack.ps1 `
#       -Stage <待打包目录> -Version <版本号> -Out <输出 zip 路径> `
#       [-MinUpgradableFrom <最低可升级起始版本>] [-AppVersion <宿主应用版本>] [-Force]
#
# 退出码: 0 = 产物就绪；非 0 = 任一步失败（就地停下）
#
# 顺序不可颠倒（TESTING.md §4）：**先写清单 → 再压缩 → 最后签名（若启用）**。
# 清单与 zip 内容不一致 ⇒ 更新器提交前逐文件哈希自检失败 ⇒ 全量回滚。
#
# 签名：本期**未启用**（RELEASE-READINESS P0-1 的决策）。传 -Sign 会明确报错而不是静默跳过。
param(
    [Parameter(Mandatory = $true)][string]$Stage,
    [Parameter(Mandatory = $true)][string]$Version,
    [Parameter(Mandatory = $true)][string]$Out,
    [string]$MinUpgradableFrom = "",
    [string]$AppVersion = "",
    [switch]$Sign,
    [switch]$Force
)

$ErrorActionPreference = "Stop"
$root = Split-Path -Parent $PSScriptRoot
$genManifest = Join-Path $PSScriptRoot "gen_manifest.ps1"

# ---------------------------------------------------------------- 0. 前置校验
if (-not (Test-Path $Stage)) { throw "待打包目录不存在: $Stage" }
$Stage = (Resolve-Path $Stage).Path

if ($Sign) {
    throw @"
-Sign 不可用：本期未启用包签名（RELEASE-READINESS P0-1 已决策）。
原因：RELEASE_PUBLIC_KEYS 为空（unsigned-build），没有可用的发布私钥。
启用步骤见 docs/DESIGN.md §6.1 —— 生成密钥对 → 内嵌公钥 → 重建 → 本脚本补签名步骤 → 走密钥轮换流程。
在此之前，包的真伪仅依赖 HTTPS 通道（见 docs/USAGE.md §9）。
"@
}

if ($AppVersion -and $AppVersion -ne $Version) {
    throw "版本不一致：-Version=$Version 与 -AppVersion=$AppVersion 不符。`n三处版本（清单 VERSION / 宿主应用版本 / release tag）必须一致，否则降级防护会误判。"
}

$Out = [IO.Path]::GetFullPath($Out)
$outDir = Split-Path -Parent $Out
if (-not (Test-Path $outDir)) { New-Item -ItemType Directory -Force -Path $outDir | Out-Null }
if ((Test-Path $Out) -and -not $Force) { throw "输出已存在: $Out（确认要覆盖请加 -Force）" }

Write-Host "=== 1/5 生成 updater.manifest ==="
if ($MinUpgradableFrom) {
    & $genManifest -Stage $Stage -Version $Version -MinUpgradableFrom $MinUpgradableFrom
} else {
    & $genManifest -Stage $Stage -Version $Version
}

Write-Host ""
Write-Host "=== 2/5 压缩（zip 内条目一律用 '/'，与更新器的归一化规则对齐）==="
Add-Type -AssemblyName System.IO.Compression | Out-Null
Add-Type -AssemblyName System.IO.Compression.FileSystem | Out-Null

function Get-Rel([string]$Base, [string]$Full) {
    return ([IO.Path]::GetRelativePath($Base, $Full)) -replace '/', '\'
}

$files = Get-ChildItem $Stage -Recurse -File | Sort-Object FullName
$fs = [IO.File]::Create($Out)
try {
    $zip = New-Object IO.Compression.ZipArchive($fs, [IO.Compression.ZipArchiveMode]::Create)
    try {
        foreach ($f in $files) {
            # 包元数据自身 + 内部保留名（含裸 .updater）不入包：更新器会把它们拦掉，白占体积
            $rel = Get-Rel $Stage $f.FullName
            if ($rel -eq '.updater' -or $rel -like '.updater\*') {
                Write-Host "  [SKIP] $rel（内部保留名）"
                continue
            }
            $entryName = $rel -replace '\\', '/'
            $entry = $zip.CreateEntry($entryName, [IO.Compression.CompressionLevel]::Optimal)
            $es = $entry.Open()
            $src = [IO.File]::OpenRead($f.FullName)
            try { $src.CopyTo($es) } finally { $src.Dispose(); $es.Dispose() }
        }
        # 空目录也写目录条目（以 '/' 结尾），保证更新器能识别并创建
        foreach ($d in (Get-ChildItem $Stage -Recurse -Directory)) {
            $rel = Get-Rel $Stage $d.FullName
            if ($rel -eq '.updater' -or $rel -like '.updater\*') { continue }
            if (@(Get-ChildItem $d.FullName -Force).Count -eq 0) {
                $zip.CreateEntry(($rel -replace '\\', '/') + '/') | Out-Null
            }
        }
    } finally { $zip.Dispose() }
} finally { $fs.Dispose() }
Write-Host "  [OK] $Out（$([math]::Round((Get-Item $Out).Length / 1KB, 1)) KB）"

Write-Host ""
Write-Host "=== 3/5 产物自检：清单必须存在，且每个 FILE 条目的声明大小与包内实际一致 ==="
$zr = [IO.Compression.ZipFile]::OpenRead($Out)
try {
    $names = @($zr.Entries | ForEach-Object { $_.FullName })
    if ($names -notcontains 'updater.manifest') { throw "产物内缺少 updater.manifest —— 版本防护与哈希自检将失效" }

    $me = $zr.GetEntry('updater.manifest')
    $mr = New-Object IO.StreamReader($me.Open())
    try { $text = $mr.ReadToEnd() } finally { $mr.Dispose() }

    $declared = @{}
    $mversion = ""
    foreach ($line in ($text -split "`n")) {
        $l = $line.TrimEnd("`r")
        if ($l -like 'FILE:*') {
            $parts = $l.Substring(5).Split('|')
            if ($parts.Count -ne 3) { throw "清单 FILE 行格式错误: $l" }
            $declared[($parts[0] -replace '\\', '/')] = [int64]$parts[1]
        } elseif ($l -like 'VERSION:*') { $mversion = $l.Substring(8) }
    }
    if ($mversion -ne $Version) { throw "清单 VERSION=$mversion 与 -Version=$Version 不符" }

    $bad = 0
    foreach ($k in $declared.Keys) {
        $e = $zr.GetEntry($k)
        if (-not $e) { Write-Host "  [ERR] 清单声明了 $k，但包内不存在"; $bad++; continue }
        if ($e.Length -ne $declared[$k]) {
            Write-Host "  [ERR] $k 大小不符：包内 $($e.Length) / 清单 $($declared[$k])"; $bad++
        }
    }
    # 反向检查：包内有、清单没有（更新器会写但没有哈希保护）
    foreach ($n in $names) {
        if ($n -eq 'updater.manifest') { continue }
        if ($n.EndsWith('/')) { continue }
        if (-not $declared.ContainsKey($n)) { Write-Host "  [ERR] 包内 $n 未出现在清单中"; $bad++ }
    }
    if ($bad -gt 0) { throw "清单与包内容不一致（$bad 处）—— 提交前哈希自检会失败并触发全量回滚" }
    Write-Host "  [OK] 清单条目 $($declared.Count) 个，全部与包内一致；VERSION=$mversion"
} finally { $zr.Dispose() }

Write-Host ""
Write-Host "=== 4/5 产物摘要 ==="
$hash = (Get-FileHash $Out -Algorithm SHA256).Hash.ToLower()
Write-Host "  zip      : $Out"
Write-Host "  sha256   : $hash"
Write-Host "  条目数   : $($files.Count) 个文件 + 空目录条目"
Write-Host "  应用侧应传: --sha256 $hash"

Write-Host ""
Write-Host "=== 5/5 签名 ==="
Write-Host "  [SKIP] 本期未启用包签名（unsigned-build）。安全边界为 0，真伪仅依赖 HTTPS 通道。"
Write-Host "         详见 docs/USAGE.md §9 与 docs/RELEASE-READINESS.md P0-1。"

Write-Host ""
Write-Host "产物就绪。上传到分发通道时请保证三通道字节一致（sha256 相同），否则用户从不同通道更新会得到不同校验结果。"
exit 0
