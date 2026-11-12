# start-agent-ui.ps1 - Start OpenChamber Agent Web UI detached on loopback:3000
# Target: Windows PowerShell 5.1. Never echoes or logs the UI password.
$ErrorActionPreference = 'Stop'

$credFile = Join-Path $PSScriptRoot 'credentials.md'
$pidFile  = Join-Path $PSScriptRoot 'agent-ui.pid'
$outLog   = Join-Path $PSScriptRoot 'agent-ui.out.log'
$errLog   = Join-Path $PSScriptRoot 'agent-ui.err.log'
$entry    = 'C:\Tools\node24\openchamber.cmd'

# Supervisor fix 2: openchamber requires opencode >= 2.2.4; the nvm/node20 global
# has 2.1.4 (employees depend on it - do not touch). A dedicated opencode 2.2.7
# lives in C:\Tools\node24; put it first on PATH for the detached child.
$env:Path = 'C:\Tools\node24;' + $env:Path

# --- 1) Read UI password from credentials.md (never print it) ---
$uiPassword = $null
if (Test-Path -LiteralPath $credFile) {
    $m = Get-Content -LiteralPath $credFile |
         Select-String -Pattern '^UI_PASSWORD=(.+)$' |
         Select-Object -First 1
    if ($m) { $uiPassword = $m.Matches[0].Groups[1].Value.Trim() }
}
if ([string]::IsNullOrEmpty($uiPassword)) {
    Write-Host '[ERROR] UI_PASSWORD missing or empty in credentials.md.' -ForegroundColor Red
    exit 1
}

# --- 2) Refuse to start if port 3000 is already listening ---
$existing = Get-NetTCPConnection -LocalPort 3000 -State Listen -ErrorAction SilentlyContinue
if ($existing) {
    Write-Host '[ERROR] Port 3000 is already in use. Run stop-agent-ui.ps1 first.' -ForegroundColor Red
    exit 2
}
if (-not (Test-Path -LiteralPath $entry)) {
    Write-Host "[ERROR] openchamber entry not found: $entry" -ForegroundColor Red
    exit 1
}

# --- 3) Detached start (supervisor fix: direct .cmd launch - the previous cmd /c
#     wrapper hit cmd's quote-stripping rule and never started openchamber;
#     password is pure hex so it needs no quoting) ---
$argList = @(
    '--port', '3000',
    '--ui-password', $uiPassword
)
$proc = Start-Process -FilePath $entry `
        -ArgumentList $argList `
        -WindowStyle Hidden `
        -RedirectStandardOutput $outLog `
        -RedirectStandardError $errLog `
        -PassThru
Set-Content -LiteralPath $pidFile -Value $proc.Id -Encoding Ascii
Write-Host ("Started openchamber wrapper, PID {0} (written to agent-ui.pid)." -f $proc.Id)

# --- 4) Poll up to ~30s: must LISTEN on loopback only ---
$deadline = (Get-Date).AddSeconds(30)
$ok = $false
while ((Get-Date) -lt $deadline) {
    Start-Sleep -Milliseconds 500
    $listen = Get-NetTCPConnection -LocalPort 3000 -State Listen -ErrorAction SilentlyContinue
    if ($listen) {
        $bad = @($listen | Where-Object { $_.LocalAddress -ne '127.0.0.1' -and $_.LocalAddress -ne '::1' })
        if ($bad.Count -gt 0) {
            $badAddr = ($bad | Select-Object -ExpandProperty LocalAddress -Unique) -join ', '
            Write-Host "[ERROR] Listening on NON-LOOPBACK address: $badAddr (e.g. 0.0.0.0 exposes the UI to the LAN)." -ForegroundColor Red
            Write-Host 'Run stop-agent-ui.ps1 immediately to shut it down.'
            exit 3
        }
        $ok = $true
        break
    }
}
if (-not $ok) {
    Write-Host '[ERROR] Port 3000 still not listening after ~30s. Log tail (password redacted):' -ForegroundColor Red
    foreach ($f in @($errLog, $outLog)) {
        if (Test-Path -LiteralPath $f) {
            Write-Host ("----- {0} -----" -f (Split-Path -Leaf $f))
            Get-Content -LiteralPath $f -Tail 15 | ForEach-Object {
                Write-Host ($_ -replace [regex]::Escape($uiPassword), '***')
            }
        }
    }
    exit 1
}

Write-Host '[OK] Agent Web UI is up on 127.0.0.1:3000 (loopback only).' -ForegroundColor Green
Write-Host "Logs: agent-ui.out.log / agent-ui.err.log. Stop with stop-agent-ui.ps1."
exit 0
