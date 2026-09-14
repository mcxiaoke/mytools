<#
.SYNOPSIS
  一键运行 FileList 端到端（E2E）浏览器自动化测试套件。

.DESCRIPTION
  基于 Playwright 和系统 Microsoft Edge 驱动，自动管理测试服务生命周期与测试夹具。
  支持透传 Playwright 参数，例如：
    .\tests\run-e2e.ps1
    .\tests\run-e2e.ps1 --headed
    .\tests\run-e2e.ps1 --ui
    .\tests\run-e2e.ps1 specs/upload.spec.js
#>

[CmdletBinding()]
param(
  [Parameter(ValueFromRemainingArguments = $true)]
  [string[]]$PlaywrightArgs
)

$ErrorActionPreference = 'Stop'
$scriptDir = Split-Path -Parent $MyInvocation.MyCommand.Path
$projectRoot = Split-Path -Parent $scriptDir
$e2eDir = Join-Path $scriptDir 'e2e'
$buildExe = Join-Path $projectRoot 'build\filelist.exe'

Write-Host "=== FileList E2E Browser Testing ===" -ForegroundColor Cyan
Write-Host "Project Root: $projectRoot"
Write-Host "E2E Dir:      $e2eDir"

# 1. 检查并编译 filelist.exe
if (-not (Test-Path $buildExe)) {
  Write-Host "[1/3] Compiling filelist.exe..." -ForegroundColor Yellow
  & (Join-Path $projectRoot 'build.ps1')
} else {
  Write-Host "[1/3] Using existing binary: $buildExe" -ForegroundColor Green
}

# 2. 检查 node_modules
$nodeModules = Join-Path $e2eDir 'node_modules'
if (-not (Test-Path $nodeModules)) {
  Write-Host "[2/3] Installing npm dependencies in tests/e2e..." -ForegroundColor Yellow
  Push-Location $e2eDir
  try {
    npm install
  } finally {
    Pop-Location
  }
} else {
  Write-Host "[2/3] npm dependencies ready" -ForegroundColor Green
}

# 3. 运行 Playwright 测试
Write-Host "[3/3] Running Playwright E2E tests..." -ForegroundColor Yellow
Push-Location $e2eDir
try {
  if ($PlaywrightArgs -and $PlaywrightArgs.Length -gt 0) {
    npx playwright test @PlaywrightArgs
  } else {
    npx playwright test
  }
  Write-Host "`n✔ All E2E tests passed successfully!" -ForegroundColor Green
} catch {
  Write-Host "`n✖ E2E tests failed!" -ForegroundColor Red
  exit 1
} finally {
  Pop-Location
}
