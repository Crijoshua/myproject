# stop-agent-ui.ps1 - Kill the openchamber process tree and free port 3000
# Target: Windows PowerShell 5.1. Safe no-op (exit 0) when nothing is running.
$ErrorActionPreference = 'Continue'

$pidFile = Join-Path $PSScriptRoot 'agent-ui.pid'

# --- 1) Kill tree by recorded PID (cmd wrapper + node children) ---
if (Test-Path -LiteralPath $pidFile) {
    $pidText = ''
    try { $pidText = (Get-Content -LiteralPath $pidFile -Raw).Trim() } catch { $pidText = '' }
    $procId = 0
    if ([int]::TryParse($pidText, [ref]$procId) -and $procId -gt 0) {
        Write-Host ("Killing process tree for PID {0} ..." -f $procId)
        & taskkill.exe /PID $procId /T /F 2>&1 | Out-Null
    }
}

# --- 2) Fallback: any process whose CommandLine mentions openchamber ---
$leftovers = @(Get-CimInstance Win32_Process -Filter "CommandLine LIKE '%openchamber%'" -ErrorAction SilentlyContinue)
foreach ($p in $leftovers) {
    if ($p.ProcessId -eq $PID) { continue }
    Write-Host ("Killing leftover openchamber process PID {0} ..." -f $p.ProcessId)
    & taskkill.exe /PID $p.ProcessId /T /F 2>&1 | Out-Null
}

# --- 3) Remove pid file ---
if (Test-Path -LiteralPath $pidFile) {
    Remove-Item -LiteralPath $pidFile -Force -ErrorAction SilentlyContinue
}

# --- 4) Poll up to ~15s until port 3000 is no longer listening ---
$deadline = (Get-Date).AddSeconds(15)
$portFree = $false
while ((Get-Date) -lt $deadline) {
    $listen = Get-NetTCPConnection -LocalPort 3000 -State Listen -ErrorAction SilentlyContinue
    if (-not $listen) { $portFree = $true; break }
    Start-Sleep -Milliseconds 500
}
if ($portFree) {
    Write-Host '[OK] Port 3000 released; Agent Web UI stopped.' -ForegroundColor Green
    exit 0
}
Write-Warning 'Port 3000 is STILL listening after ~15s. Something else may hold it. Inspect with: Get-NetTCPConnection -LocalPort 3000'
exit 1
