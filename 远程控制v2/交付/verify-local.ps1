# verify-local.ps1 - Verify Agent Web UI: loopback-only, serve reachable, auth enforced
# Target: Windows PowerShell 5.1. Any FAIL -> non-zero exit. Read-only checks only.
$ErrorActionPreference = 'Continue'

$serveUrl = 'https://<PC_NAME>.<TAILNET>.ts.net'
$localUrl = 'http://127.0.0.1:3000'
$anyFail  = $false

function Get-HttpStatus {
    param([string]$Url)
    try {
        $resp = Invoke-WebRequest -Uri $Url -UseBasicParsing -TimeoutSec 15 -MaximumRedirection 0 -ErrorAction Stop
        return @{ Code = [int]$resp.StatusCode; Location = $resp.Headers['Location'] }
    } catch {
        if ($_.Exception.Response) {
            return @{ Code = [int]$_.Exception.Response.StatusCode; Location = $_.Exception.Response.Headers['Location'] }
        }
        return $null
    }
}

# --- Check 1: port 3000 must listen on loopback only ---
Write-Host '== Check 1: loopback-only listening on port 3000 =='
$listen = Get-NetTCPConnection -LocalPort 3000 -State Listen -ErrorAction SilentlyContinue
if (-not $listen) {
    Write-Host 'FAIL - nothing is listening on port 3000. Run start-agent-ui.ps1 first.' -ForegroundColor Red
    $anyFail = $true
} else {
    $bad = @($listen | Where-Object { $_.LocalAddress -ne '127.0.0.1' -and $_.LocalAddress -ne '::1' })
    if ($bad.Count -eq 0) {
        $addrs = ($listen | Select-Object -ExpandProperty LocalAddress -Unique) -join ', '
        Write-Host "PASS - listening on loopback only ($addrs)."
    } else {
        $badAddr = ($bad | Select-Object -ExpandProperty LocalAddress -Unique) -join ', '
        Write-Host "FAIL - listening on non-loopback address: $badAddr (0.0.0.0 would expose the UI to the LAN). Run stop-agent-ui.ps1." -ForegroundColor Red
        $anyFail = $true
    }
}

# --- Check 2: reachable through Tailscale Serve (443 -> 3000) ---
Write-Host ''
Write-Host '== Check 2: reachable via Tailscale Serve =='
$serveSkipped  = $false
$serveReachable = $false
$serveConfigured = $true
try {
    $tsText = (& tailscale.exe serve status 2>&1 | Out-String).Trim()
    if ($tsText -match 'No serve config') { $serveConfigured = $false }
} catch {
    # tailscale.exe unavailable - cannot pre-check; fall through to the HTTP probe
}
if (-not $serveConfigured) {
    Write-Host "SKIP - tailscale serve is not configured yet. Complete the manual admin-console step, then run: tailscale serve --bg 3000"
    $serveSkipped = $true
} else {
    $r = Get-HttpStatus -Url $serveUrl
    if ($null -eq $r) {
        Write-Host "FAIL - $serveUrl unreachable (serve may be misconfigured or the tailnet is down)."
        Write-Host "  NOTE (supervisor): under the minimal grants (only tag:mobile -> tag:server tcp:443),"
        Write-Host "  a probe FROM this machine (tag:server) is EXPECTED to be denied - the authoritative"
        Write-Host "  Check 2 must run from an authorized device (the iPhone, tag:mobile). Treat this FAIL"
        Write-Host "  as expected if serve status shows the proxy; it doubles as A2-2 deny-by-default evidence."
        $anyFail = $true
    } else {
        Write-Host ("PASS - {0} responded with HTTP {1} (serve proxy is working)." -f $serveUrl, $r.Code)
        $serveReachable = $true
    }
}

# --- Check 3: unauthenticated GET must be rejected ---
# Supervisor note (per ACCEPTANCE-REPORT A3): probe the API layer (/api/config),
# not the SPA shell - the shell intentionally returns 200 (PWA design), the
# password gate lives on /api/* and /auth/*.
Write-Host ''
Write-Host '== Check 3: unauthenticated request is rejected =='
$target = "$localUrl/api/config"
if ($serveReachable) { $target = "$serveUrl/api/config" }
Write-Host ("Probing: {0}" -f $target)
$r = Get-HttpStatus -Url $target
if ($null -eq $r) {
    Write-Host "SKIP - $target is not reachable at all; fix the failures above and re-run."
} elseif ($r.Code -eq 401 -or $r.Code -eq 403) {
    Write-Host ("PASS - unauthenticated GET returned {0} (rejected)." -f $r.Code)
} elseif ($r.Code -ge 300 -and $r.Code -lt 400) {
    Write-Host ("PASS - unauthenticated GET returned {0} redirect to login page ({1})." -f $r.Code, $r.Location)
} elseif ($r.Code -eq 200) {
    Write-Host "FAIL - unauthenticated GET returned 200: UI password is NOT in effect. Run stop-agent-ui.ps1 and restart with a valid password." -ForegroundColor Red
    $anyFail = $true
} else {
    Write-Host ("FAIL - unexpected HTTP {0} from unauthenticated GET; verify serve config and the openchamber process." -f $r.Code) -ForegroundColor Red
    $anyFail = $true
}

Write-Host ''
if ($anyFail) {
    Write-Host 'RESULT: FAILED (see FAIL lines above).' -ForegroundColor Red
    exit 1
}
Write-Host 'RESULT: all executed checks passed (SKIPs noted above).' -ForegroundColor Green
exit 0
