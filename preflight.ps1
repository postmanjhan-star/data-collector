[CmdletBinding()]
param([switch]$CheckScanner)
$ErrorActionPreference = 'Stop'
Write-Host '=== HF811 Windows 10 x64 preflight ===' -ForegroundColor Cyan
$os = Get-CimInstance Win32_OperatingSystem
Write-Host "OS: $($os.Caption) | version: $($os.Version) | architecture: $($os.OSArchitecture)"
if (-not $os.Version.StartsWith('10.0.')) { Write-Warning 'Expected Windows 10; validate OS edition manually.' }
if ($os.OSArchitecture -notmatch '64') { throw 'This installer supports only x64 Windows.' }
if ($os.Caption -notmatch 'Windows 10') { Write-Warning 'Target system was specified as Windows 10; verify edition and build.' }
Write-Warning 'In 2026, Windows 10 Home/Pro are out of general support. Check ESU/LTSC and enterprise policy.'
$folder = Join-Path $env:LOCALAPPDATA 'HF811Collector'
$configPath = Join-Path $folder 'config.json'
if (-not (Test-Path $configPath)) { Write-Warning "Config not found. Install the app first: $configPath"; return }
$config = Get-Content -LiteralPath $configPath -Raw | ConvertFrom-Json
Write-Host "TCP: Mode=$($config.Mode) Host=$($config.Host) Port=$($config.Port) Framing=$($config.Framing)"
Write-Host "Data: $(Join-Path $folder 'data\scans.sqlite3')"
Write-Host "Logs: $(Join-Path $folder 'logs')"
if ($config.Framing -eq 'idle') { Write-Warning 'idle framing is approximate. Configure the scanner to append LF or CRLF for actual use.' }
if ($CheckScanner -and $config.Mode -eq 'connect') {
    Write-Host "Checking TCP connectivity to $($config.Host):$($config.Port)..."
    $ok = Test-NetConnection -ComputerName $config.Host -Port $config.Port -InformationLevel Quiet
    if ($ok) { Write-Host 'Scanner TCP port reachable' -ForegroundColor Green }
    else { Write-Warning 'Scanner port unreachable; verify cabling, subnet, IP and TCP Server mode.' }
}
try {
    $resp = Invoke-RestMethod -Uri "http://127.0.0.1:$($config.ApiPort)/health" -TimeoutSec 2
    Write-Host "Local collector API: $($resp.status)" -ForegroundColor Green
} catch { Write-Warning 'Local API unavailable; Collector may not be running.' }
