param(
    [string]$BaseUrl = "",
    [string]$Username = "",
    [string]$Password = "",
    [switch]$NoAutoStart
)

$ErrorActionPreference = "Stop"

if ($BaseUrl) {
    $env:MOBILE_SMOKE_BASE_URL = $BaseUrl
}

if ($Username) {
    $env:MOBILE_SMOKE_USERNAME = $Username
}

if ($Password) {
    $env:MOBILE_SMOKE_PASSWORD = $Password
}

if ($NoAutoStart) {
    $env:MOBILE_SMOKE_AUTOSTART = "0"
}

Write-Host "Running mobile smoke verification..."
python verify_mobile_smoke.py
$exitCode = $LASTEXITCODE

if ($exitCode -ne 0) {
    Write-Host "Mobile smoke verification failed (exit code $exitCode)." -ForegroundColor Red
    exit $exitCode
}

Write-Host "Mobile smoke verification passed."
