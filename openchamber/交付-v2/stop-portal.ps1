# R1 中文门户页停止脚本（v2 独立）
$V2Root = $PSScriptRoot
$PidFile = Join-Path $V2Root "portal.pid"

if (-not (Test-Path $PidFile)) {
    Write-Host "[portal] 未在运行" -ForegroundColor Yellow
    exit 0
}

$pidValue = Get-Content $PidFile -ErrorAction SilentlyContinue
if ($pidValue -and (Get-Process -Id $pidValue -ErrorAction SilentlyContinue)) {
    Stop-Process -Id $pidValue -Force
    Write-Host "[portal] 已停止 (pid=$pidValue)" -ForegroundColor Green
}
Remove-Item $PidFile -ErrorAction SilentlyContinue
