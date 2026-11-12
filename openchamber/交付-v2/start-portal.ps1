# R1 中文门户页启动脚本（v2 独立，不影响 1.0 任何服务）
$ErrorActionPreference = "Stop"

$V2Root = $PSScriptRoot                           # 交付-v2
$PortalDir = Join-Path $V2Root "portal"
$PidFile   = Join-Path $V2Root "portal.pid"
$OutLog    = Join-Path $V2Root "portal.out.log"
$ErrLog    = Join-Path $V2Root "portal.err.log"

if (Test-Path $PidFile) {
    $old = Get-Content $PidFile -ErrorAction SilentlyContinue
    if ($old -and (Get-Process -Id $old -ErrorAction SilentlyContinue)) {
        Write-Host "[portal] 已在运行 (pid=$old)，跳过启动" -ForegroundColor Yellow
        exit 0
    }
}

$proc = Start-Process -FilePath "node" `
    -ArgumentList "server.js" `
    -WorkingDirectory $PortalDir `
    -WindowStyle Hidden `
    -RedirectStandardOutput $OutLog `
    -RedirectStandardError $ErrLog `
    -PassThru

$proc.Id | Out-File -Encoding ascii -NoNewline $PidFile
Start-Sleep -Seconds 1

if ($proc.HasExited) {
    Write-Host "[portal] 启动失败，错误日志：" -ForegroundColor Red
    Get-Content $ErrLog
    exit 1
}

Write-Host "[portal] 已启动 pid=$($proc.Id) → http://127.0.0.1:9800" -ForegroundColor Green
