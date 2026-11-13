# ============================================================
# stop-cmd.ps1 —— R2 迭代：停止由 start-cmd.ps1 启动的 ttyd 进程
#
# 行为：
#   1. 读取同目录 cmd.pid 中记录的进程号；
#   2. 停止对应的 ttyd 进程，然后删除 cmd.pid；
#   3. pid 文件不存在、内容无效或进程已不存在时，均视为正常结束
#      （退出码 0），不报错。
#
# 使用建议：本脚本为 UTF-8（无 BOM）编码，请使用 PowerShell 7+（pwsh）运行。
# ============================================================

$pidFile = Join-Path $PSScriptRoot 'cmd.pid'

# ---------- 1. pid 文件不存在：无事可做，正常退出 ----------
if (-not (Test-Path -LiteralPath $pidFile)) {
    Write-Host '未找到 cmd.pid，没有需要停止的 ttyd 进程。'
    exit 0
}

# ---------- 2. 读取并解析 pid ----------
$raw = Get-Content -LiteralPath $pidFile -ErrorAction SilentlyContinue | Select-Object -First 1
$targetPid = 0

if ([int]::TryParse("$raw".Trim(), [ref]$targetPid) -and $targetPid -gt 0) {
    $proc = Get-Process -Id $targetPid -ErrorAction SilentlyContinue
    if ($null -ne $proc) {
        if ($proc.ProcessName -ine 'ttyd') {
            Write-Host "cmd.pid 中的 PID $targetPid 已被其他进程（$($proc.ProcessName)）复用，不停止，仅清理 pid 文件。"
        }
        else {
        # 进程仍在且确为 ttyd：停止它
        Stop-Process -Id $targetPid -Force -ErrorAction SilentlyContinue
        $stopped = $false
        for ($i = 0; $i -lt 20; $i++) {
            if ($null -eq (Get-Process -Id $targetPid -ErrorAction SilentlyContinue)) {
                $stopped = $true
                break
            }
            Start-Sleep -Milliseconds 100
        }
        if ($stopped) {
            Write-Host "ttyd 进程（PID: $targetPid）已停止。"
        }
        else {
            Write-Host "已向 PID $targetPid 发送停止指令，但进程尚未完全退出，请稍后确认。"
        }
        }
    }
    else {
        Write-Host "cmd.pid 记录的进程（PID: $targetPid）已不存在，无需停止。"
    }
}
else {
    Write-Host 'cmd.pid 内容无效（不是进程号），仅清理该文件。'
}

# ---------- 3. 删除 pid 文件（无论上述结果如何都执行，失败不影响退出码） ----------
Remove-Item -LiteralPath $pidFile -Force -ErrorAction SilentlyContinue

# ---------- 4. 顺带停止网关（ws-gateway.js / node，与 ttyd 同生命周期） ----------
$gwPidFile = Join-Path $PSScriptRoot 'gateway.pid'
if (Test-Path -LiteralPath $gwPidFile) {
    $gwRaw = Get-Content -LiteralPath $gwPidFile -ErrorAction SilentlyContinue | Select-Object -First 1
    $gwPid = 0
    if ([int]::TryParse("$gwRaw".Trim(), [ref]$gwPid) -and $gwPid -gt 0) {
        $gwProc = Get-Process -Id $gwPid -ErrorAction SilentlyContinue
        if ($null -ne $gwProc) {
            if ($gwProc.ProcessName -ine 'node') {
                Write-Host "gateway.pid 中的 PID $gwPid 已被其他进程（$($gwProc.ProcessName)）复用，不停止，仅清理 pid 文件。"
            }
            else {
                Stop-Process -Id $gwPid -Force -ErrorAction SilentlyContinue
                Write-Host "网关进程（PID: $gwPid）已停止。"
            }
        }
        else {
            Write-Host "gateway.pid 记录的进程（PID: $gwPid）已不存在，无需停止。"
        }
    }
    Remove-Item -LiteralPath $gwPidFile -Force -ErrorAction SilentlyContinue
}
exit 0
