# updater.manifest 生成（TESTING.md §4）
#
# 用法:
#   powershell -ExecutionPolicy Bypass -File scripts\gen_manifest.ps1 `
#       -Stage <待打包目录> -Version <版本号> [-MinUpgradableFrom <最低可升级起始版本>]
#
# 生成的文件写在 <Stage>\updater.manifest。
#
# 关键约束（顺序不可颠倒）：**先写清单 → 再压缩 → 最后对 zip 签名**。
# 清单与 zip 内容不一致时，提交前逐文件哈希自检会失败并触发全量回滚。
param(
    [Parameter(Mandatory = $true)][string]$Stage,
    [Parameter(Mandatory = $true)][string]$Version,
    [string]$MinUpgradableFrom = ""
)

$ErrorActionPreference = "Stop"

if (-not (Test-Path $Stage)) { throw "待打包目录不存在: $Stage" }
$Stage = (Resolve-Path $Stage).Path

$lines = @("MANIFEST:1", "VERSION:$Version")
if ($MinUpgradableFrom) { $lines += "MIN_UPGRADABLE_FROM:$MinUpgradableFrom" }

# 相对路径的分隔符**必须**归一化为 `\`：本脚本可能在 Linux/pwsh 上执行，
# 此时 GetRelativePath 产出 `/`，与磁盘侧拼接出的 `\` 不一致会导致
# "清单条目在磁盘上找不到" → 误判自检失败 → 全量回滚。
function Get-Rel([string]$Base, [string]$Full) {
    return ([IO.Path]::GetRelativePath($Base, $Full)) -replace '/', '\'
}

# 包元数据自身 + 内部保留名（含裸 .updater）均不入清单
function Test-Excluded([string]$Rel) {
    return ($Rel -eq 'updater.manifest' -or $Rel -eq '.updater' -or $Rel -like '.updater\*')
}

Get-ChildItem $Stage -Recurse -File | Sort-Object FullName | ForEach-Object {
    $rel = Get-Rel $Stage $_.FullName
    if (Test-Excluded $rel) { return }
    $h = (Get-FileHash $_.FullName -Algorithm SHA256).Hash.ToLower()
    $lines += "FILE:$rel|$($_.Length)|$h"
}

Get-ChildItem $Stage -Recurse -Directory | Sort-Object FullName | ForEach-Object {
    $rel = Get-Rel $Stage $_.FullName
    if (Test-Excluded $rel) { return }
    $lines += "DIR:$rel"
}

$out = Join-Path $Stage 'updater.manifest'
# LF 结尾、UTF-8 无 BOM
$enc = New-Object System.Text.UTF8Encoding($false)
[System.IO.File]::WriteAllText($out, ($lines -join "`n") + "`n", $enc)

Write-Host "[OK] 已生成 $out"
Write-Host "     VERSION=$Version  MIN_UPGRADABLE_FROM=$(if ($MinUpgradableFrom) { $MinUpgradableFrom } else { '(未设置)' })"
Write-Host "     条目数=$($lines.Count - 2)（FILE/DIR 合计）"
Write-Host "     下一步：压缩该目录，然后对 zip 签名 —— 顺序不可颠倒。"
