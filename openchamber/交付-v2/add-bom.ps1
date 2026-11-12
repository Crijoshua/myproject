$ErrorActionPreference = "Stop"
$targets = @(
    (Join-Path $PSScriptRoot "start-portal.ps1"),
    (Join-Path $PSScriptRoot "stop-portal.ps1")
)
foreach ($f in $targets) {
    $c = [IO.File]::ReadAllText($f, [Text.Encoding]::UTF8)
    [IO.File]::WriteAllText($f, $c, (New-Object Text.UTF8Encoding $true))
    Write-Output "BOM added: $f"
}
