param(
    [string]$BaseUrl = "",
    [string]$Username = "",
    [string]$Password = "",
    [string]$Out = "docs/assets/readme",
    [string]$DesktopViewport = "1366x768",
    [string]$MobileViewport = "390x844",
    [switch]$NoAutoStart
)

$ErrorActionPreference = "Stop"

$args = @(
    "capture_readme_screenshots.py",
    "--out", $Out,
    "--desktop-viewport", $DesktopViewport,
    "--mobile-viewport", $MobileViewport
)

if ($BaseUrl) {
    $args += @("--base-url", $BaseUrl)
}

if ($Username) {
    $args += @("--username", $Username)
}

if ($Password) {
    $args += @("--password", $Password)
}

if ($NoAutoStart) {
    $args += "--no-autostart"
}

Write-Host "Capturing README screenshots..."
python @args
$exitCode = $LASTEXITCODE

if ($exitCode -ne 0) {
    Write-Host "README screenshot capture failed (exit code $exitCode)." -ForegroundColor Red
    exit $exitCode
}

Write-Host "README screenshot capture completed."
