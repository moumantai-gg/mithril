[CmdletBinding()]
param(
    [switch]$Force,
    [switch]$IgnoreDirty
)

$ErrorActionPreference = 'Stop'

Push-Location (Split-Path -Parent $PSScriptRoot)
try {
    $exe   = "src/Mithril.Shell/bin/Debug/net10.0-windows/Mithril.exe"
    $stamp = "src/Mithril.Shell/bin/Debug/net10.0-windows/.last-build-sha"

    $head      = (git rev-parse HEAD).Trim()
    $last      = if (Test-Path $stamp) { (Get-Content $stamp -Raw).Trim() } else { '' }
    $shortHead = $head.Substring(0, 7)
    $shortLast = if ($last) { $last.Substring(0, 7) } else { '(none)' }

    $reasons = @()
    if ($Force)                { $reasons += 'forced (-Force)' }
    if (-not (Test-Path $exe)) { $reasons += 'exe missing' }
    if ($head -ne $last)       { $reasons += "new commit ($shortLast -> $shortHead)" }
    if (-not $IgnoreDirty) {
        $dirty = git status --porcelain
        if ($dirty) { $reasons += 'dirty tree' }
    }

    if ($reasons.Count -gt 0) {
        Write-Host "[start] Rebuilding: $($reasons -join ', ')" -ForegroundColor Cyan
        dotnet clean ./Mithril.slnx
        if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
        dotnet build ./Mithril.slnx
        if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
        Set-Content -Path $stamp -Value $head
    } else {
        Write-Host "[start] Up to date ($shortHead); launching..." -ForegroundColor DarkGray
    }

    & $exe
} finally {
    Pop-Location
}
