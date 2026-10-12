<#
.SYNOPSIS
    实时采样 WorkBuddy、Hermes 和 豆包 的系统资源占用，统计指定时长（默认 5 分钟）的平均值与峰值。

.DESCRIPTION
    每隔指定间隔（默认 5 秒）获取目标进程家族的全部子进程与运行时指标：
    - CPU 占用率 (动态微分计算并按逻辑核心数归一化)
    - 物理内存占用 (Working Set / 实际占用物理 RAM)
    - 专用提交内存 (Private Bytes / 虚拟内存提交)
    - 线程总数 (Threads)
    - 句柄总数 (Handles)
    - 活跃进程数 (Process Count)
    采样完成后输出统计表格，并可保存原始样本数据到 CSV。

.EXAMPLE
    pwsh C:\Users\mcxiaoke\sample_app_resources.ps1
    pwsh C:\Users\mcxiaoke\sample_app_resources.ps1 -DurationMinutes 5 -IntervalSeconds 5
#>

[CmdletBinding()]
param(
    [Parameter(Position = 0)]
    [double]$DurationMinutes = 5,

    [Parameter(Position = 1)]
    [int]$IntervalSeconds = 5,

    [int]$DurationSeconds = 0,

    [string]$CsvPath = "$env:TEMP\app_resource_sample_$(Get-Date -Format 'yyyyMMdd_HHmmss').csv"
)

# 确保控制台支持 UTF-8
[Console]::OutputEncoding = [System.Text.Encoding]::UTF8

$cores = [Environment]::ProcessorCount
$totalSeconds = if ($DurationSeconds -gt 0) { $DurationSeconds } else { [int]($DurationMinutes * 60) }
$expectedSamples = [math]::Ceiling($totalSeconds / $IntervalSeconds)

Write-Host "================================================================================" -ForegroundColor Cyan
Write-Host " 目标应用资源采样器 (WorkBuddy vs Hermes vs 豆包 vs OpenCode)" -ForegroundColor Cyan
Write-Host " 采样总时长: $DurationMinutes 分钟 ($totalSeconds 秒) | 采样间隔: $IntervalSeconds 秒 | 预计采样: $expectedSamples 次" -ForegroundColor Gray
Write-Host " 本机逻辑核心数: $cores 核心" -ForegroundColor Gray
Write-Host " CSV 输出路径: $CsvPath" -ForegroundColor Gray
Write-Host "================================================================================" -ForegroundColor Cyan
Write-Host ""

# 监控目标应用定义（使用路径特征精准匹配主进程及其派生运行时/沙箱）
$targets = @(
    @{
        Name = "WorkBuddy"
        Matcher = {
            param($proc)
            $p = $proc.Path
            $n = $proc.ProcessName
            if ($n -in @('pwsh', 'powershell', 'conhost')) { return $false }
            return ($p -like "*WorkBuddy*" -or $p -like "*\.workbuddy\*")
        }
    },
    @{
        Name = "Hermes"
        Matcher = {
            param($proc)
            $p = $proc.Path
            $n = $proc.ProcessName
            if ($n -in @('pwsh', 'powershell', 'conhost')) { return $false }
            return ($p -like "*\hermes\*" -or $n -eq "Hermes" -or $n -eq "hermes")
        }
    },
    @{
        Name = "Doubao"
        Matcher = {
            param($proc)
            $p = $proc.Path
            $n = $proc.ProcessName
            if ($n -in @('pwsh', 'powershell', 'conhost')) { return $false }
            return ($p -like "*\Doubao\*" -or $n -eq "Doubao" -or $n -eq "aha_doctor")
        }
    },
    @{
        Name = "OpenCode"
        Matcher = {
            param($proc)
            $p = $proc.Path
            $n = $proc.ProcessName
            if ($n -in @('pwsh', 'powershell', 'conhost')) { return $false }
            return ($p -like "*opencode*" -or $n -like "*opencode*")
        }
    }
)

# 历史 CPU 时间戳字典 [PID] -> TotalProcessorTime.TotalMilliseconds
$lastCpuTimes = @{}
$samples = [System.Collections.Generic.List[PSCustomObject]]::new()

$stopwatch = [System.Diagnostics.Stopwatch]::StartNew()
$wallTimer = [System.Diagnostics.Stopwatch]::StartNew()
$sampleIndex = 0
$realWallElapsedMs = 0

try {
    while ($stopwatch.Elapsed.TotalSeconds -lt $totalSeconds) {
        $loopStart = [System.Diagnostics.Stopwatch]::StartNew()
        $now = Get-Date
        $sampleIndex++

        if ($sampleIndex -gt 1) {
            $realWallElapsedMs = $wallTimer.ElapsedMilliseconds
            $wallTimer.Restart()
        }

        # 获取系统全部进程快照
        $allProcs = Get-Process -ErrorAction SilentlyContinue

        $liveStatus = @{}

        foreach ($target in $targets) {
            $name = $target.Name
            $matched = $allProcs | Where-Object { & $target.Matcher $_ }

            $workingSetSum = 0
            $privateMemSum = 0
            $handlesSum = 0
            $threadsSum = 0
            $procCount = $matched.Count
            $deltaCpuMs = 0

            foreach ($p in $matched) {
                $pid_ = $p.Id
                $workingSetSum += $p.WorkingSet64
                $privateMemSum += $p.PrivateMemorySize64
                $handlesSum += $p.HandleCount
                $threadsSum += $p.Threads.Count

                try {
                    $currCpuMs = $p.TotalProcessorTime.TotalMilliseconds
                    if ($lastCpuTimes.ContainsKey($pid_)) {
                        $diff = $currCpuMs - $lastCpuTimes[$pid_]
                        if ($diff -gt 0) { $deltaCpuMs += $diff }
                    }
                    $lastCpuTimes[$pid_] = $currCpuMs
                } catch {
                    # 进程可能在采样瞬间退出
                }
            }

            # 首次采样由于无历史基准点，CPU 算作 0，第 2 次开始按真实时钟差值计算
            $cpuPercent = 0
            if ($sampleIndex -gt 1 -and $realWallElapsedMs -gt 0) {
                # 采样区间实际毫秒数 * 核心数（与任务管理器 100% 满载归一化逻辑一致）
                $normTime = $realWallElapsedMs * $cores
                $cpuPercent = [math]::Round(($deltaCpuMs / $normTime) * 100, 2)
            }

            $wsMb = [math]::Round($workingSetSum / 1MB, 2)
            $pmMb = [math]::Round($privateMemSum / 1MB, 2)

            $record = [PSCustomObject]@{
                Timestamp    = $now.ToString("yyyy-MM-dd HH:mm:ss")
                SampleIndex  = $sampleIndex
                App          = $name
                ProcessCount = $procCount
                CpuPercent   = $cpuPercent
                WorkingSetMB = $wsMb
                PrivateMemMB = $pmMb
                Threads      = $threadsSum
                Handles      = $handlesSum
            }
            $samples.Add($record)

            $liveStatus[$name] = [PSCustomObject]@{
                Cpu = $cpuPercent
                Ram = $wsMb
                Procs = $procCount
            }
        }

        # 清理已退出的 PID 缓存
        $activePids = $allProcs.Id
        $stalePids = $lastCpuTimes.Keys | Where-Object { $_ -notin $activePids }
        foreach ($sp in $stalePids) { $lastCpuTimes.Remove($sp) }

        # 实时单行终端刷新显示
        $elapsed = $stopwatch.Elapsed
        $remaining = [TimeSpan]::FromSeconds([math]::Max(0, $totalSeconds - $elapsed.TotalSeconds))
        
        $statusParts = foreach ($t in $targets) {
            $st = $liveStatus[$t.Name]
            "{0}: {1,4:N1}% | {2,4:N0}MB" -f $t.Name, $st.Cpu, $st.Ram
        }
        $progLine = "[{0:mm\:ss}/{1:mm\:ss}]  {2}  " -f $elapsed, [TimeSpan]::FromSeconds($totalSeconds), ($statusParts -join "  |  ")
        Write-Host -NoNewline "`r$progLine"

        # 控制精准间隔休眠
        $loopElapsedMs = $loopStart.ElapsedMilliseconds
        $sleepMs = ($IntervalSeconds * 1000) - $loopElapsedMs
        if ($sleepMs -gt 0 -and ($stopwatch.Elapsed.TotalSeconds -lt $totalSeconds)) {
            Start-Sleep -Milliseconds $sleepMs
        }
    }
} finally {
    $stopwatch.Stop()
    Write-Host "`n"
}

# 保存原始数据到 CSV
if ($samples.Count -gt 0) {
    $samples | Export-Csv -Path $CsvPath -NoTypeInformation -Encoding utf8
}

Write-Host "================================================================================" -ForegroundColor Green
Write-Host " 采样完成！聚合统计报告" -ForegroundColor Green
Write-Host "================================================================================" -ForegroundColor Green

# 过滤掉第 1 次 CPU 基准采样点后进行统计
$validSamples = $samples | Where-Object { $_.SampleIndex -gt 1 }

$summary = foreach ($t in $targets) {
    $appSamples = $validSamples | Where-Object { $_.App -eq $t.Name }
    if ($appSamples.Count -gt 0) {
        $cpuStats = $appSamples | Measure-Object -Property CpuPercent -Average -Maximum -Minimum
        $wsStats  = $appSamples | Measure-Object -Property WorkingSetMB -Average -Maximum -Minimum
        $pmStats  = $appSamples | Measure-Object -Property PrivateMemMB -Average -Maximum
        $thStats  = $appSamples | Measure-Object -Property Threads -Average
        $hdStats  = $appSamples | Measure-Object -Property Handles -Average
        $pcStats  = $appSamples | Measure-Object -Property ProcessCount -Average

        [PSCustomObject]@{
            "应用名称"          = $t.Name
            "平均 CPU (%)"     = [math]::Round($cpuStats.Average, 2)
            "峰值 CPU (%)"     = [math]::Round($cpuStats.Maximum, 2)
            "平均物理内存 (MB)"  = [math]::Round($wsStats.Average, 1)
            "峰值物理内存 (MB)"  = [math]::Round($wsStats.Maximum, 1)
            "平均提交内存 (MB)"  = [math]::Round($pmStats.Average, 1)
            "平均线程数"       = [int][math]::Round($thStats.Average, 0)
            "平均句柄数"       = [int][math]::Round($hdStats.Average, 0)
            "平均进程数"       = [math]::Round($pcStats.Average, 1)
        }
    }
}

$summary | Format-Table -AutoSize
Write-Host "原始采样数据已保存至: $CsvPath" -ForegroundColor Yellow
Write-Host "================================================================================" -ForegroundColor Gray
