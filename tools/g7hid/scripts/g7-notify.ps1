$ErrorActionPreference = "Stop"

# PowerShell 7 对 WinRT 通知类型的加载不稳定；在 PowerShell 7 中自动转到 Windows PowerShell 5.1 执行。
if ($PSVersionTable.PSEdition -eq "Core") {
    & powershell.exe -NoProfile -ExecutionPolicy Bypass -File $PSCommandPath @args
    exit $LASTEXITCODE
}

try {
    [Console]::OutputEncoding = [System.Text.Encoding]::UTF8
} catch { }

function Write-ErrorLog {
    param([string]$Message)

    $timestamp = Get-Date -Format "yyyy-MM-dd HH:mm:ss"
    [Console]::Error.WriteLine("[$timestamp] $Message")
}

try {
    $g7Command = Get-Command -Name "g7hid.exe" -ErrorAction SilentlyContinue |
        Select-Object -First 1

    if ($g7Command) {
        $exe = $g7Command.Source
    } else {
        $localExe = Join-Path $PSScriptRoot "g7hid.exe"

        if (Test-Path -LiteralPath $localExe -PathType Leaf) {
            $exe = $localExe
        } else {
            throw "找不到 g7hid.exe：请确认它已加入 PATH，或将它放在脚本目录 $PSScriptRoot。"
        }
    }

    $raw = & $exe

    if ($LASTEXITCODE -ne 0) {
        throw "g7hid.exe 执行失败，退出代码 $LASTEXITCODE。"
    }

    $rawText = ($raw | Out-String).Trim()
    $battery = 0

    if (-not [int]::TryParse($rawText, [ref]$battery)) {
        throw "无法解析电量输出：$rawText"
    }

    if ($battery -lt 0 -or $battery -gt 100) {
        throw "电量值超出范围：$battery"
    }

    # g7hid.exe 约定：读取失败或鼠标休眠未就绪时返回 0，此时忽略退出即可。
    if ($battery -le 0) {
        exit 0
    }

    if ($battery -gt 20) {
        exit 0
    }

    $title = "G7 鼠标电量低"
    $message = "当前电量 $battery%，请连接充电线。"

    # 弹 Windows 通知，不需要安装额外模块
    [void][Windows.UI.Notifications.ToastNotificationManager, Windows.UI.Notifications, ContentType = WindowsRuntime]
    [void][Windows.Data.Xml.Dom.XmlDocument, Windows.Data.Xml.Dom, ContentType = WindowsRuntime]

    $appId = "{1AC14E77-02E7-4E5D-B744-2EB1AE5198B7}\WindowsPowerShell\v1.0\powershell.exe"

    $xml = @"
<toast>
  <visual>
    <binding template="ToastGeneric">
      <text>$title</text>
      <text>$message</text>
    </binding>
  </visual>
</toast>
"@

    $doc = New-Object Windows.Data.Xml.Dom.XmlDocument
    $doc.LoadXml($xml)

    $toast = New-Object Windows.UI.Notifications.ToastNotification -ArgumentList $doc
    [Windows.UI.Notifications.ToastNotificationManager]::CreateToastNotifier($appId).Show($toast)

    # 可选：配置了 Slack 兼容 Webhook 时额外推送
    if ($env:G7_BATTERY_WEBHOOK) {
        try {
            $payload = @{ text = "$title：$message" } | ConvertTo-Json -Compress
            Invoke-RestMethod -Method Post -Uri $env:G7_BATTERY_WEBHOOK `
                -ContentType "application/json" -Body $payload | Out-Null
        } catch {
            throw "Webhook 推送失败：$($_.Exception.Message)"
        }
    }
} catch {
    Write-ErrorLog "ERROR: $($_.Exception.Message)"
    exit 1
}
