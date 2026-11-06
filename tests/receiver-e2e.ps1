[CmdletBinding()]
param()
$ErrorActionPreference = 'Stop'
if ($env:GITHUB_ACTIONS -ne 'true') {
    throw 'Receiver integration test starts network services and is restricted to disposable CI runners.'
}

Set-Location (Join-Path $PSScriptRoot '..')
$env:HF811_SERVER_API_KEY = 'ci-only-not-a-production-secret-2026'
$env:HF811_SYNC_API_KEY = $env:HF811_SERVER_API_KEY
$env:HF811_RECEIVER_DB = Join-Path $env:RUNNER_TEMP 'hf811-ci-receiver.sqlite3'
$url = 'http://127.0.0.1:18090/api/v1/collector/scans/batch'
$health = 'http://127.0.0.1:18090/health'
$output = Join-Path $env:RUNNER_TEMP 'hf811-receiver-out.txt'
$errors = Join-Path $env:RUNNER_TEMP 'hf811-receiver-err.txt'

Write-Host 'Publishing reference receiver...' -ForegroundColor Cyan
& dotnet publish .\server\HF811Receiver.csproj -c Release -o .\dist\receiver
if ($LASTEXITCODE -ne 0) { throw 'Receiver build failed' }
$assembly = (Resolve-Path .\dist\receiver\HF811Receiver.dll).Path
$server = $null
try {
    $server = Start-Process -FilePath 'dotnet' -ArgumentList @(
        $assembly, '--urls', 'http://127.0.0.1:18090'
    ) -PassThru -RedirectStandardOutput $output -RedirectStandardError $errors

    $ready = $false
    for ($i = 0; $i -lt 40; $i++) {
        if ($server.HasExited) { throw "Receiver exited $($server.ExitCode). See $errors" }
        try
        {
            $response = Invoke-RestMethod -Uri $health -TimeoutSec 2
            if ($response.status -eq 'ok') { $ready = $true; break }
        }
        catch { Start-Sleep -Milliseconds 350 }
    }
    if (-not $ready) { throw "Receiver health check timed out. See $errors" }

    Write-Host 'Testing Collector -> HTTP receiver -> ACK + idempotent replay...' -ForegroundColor Cyan
    & .\dist\win-x64\HF811Collector.exe sync-e2e --url $url
    if ($LASTEXITCODE -ne 0) { throw 'Collector-to-receiver integration test failed' }

    $headers = @{ 'X-Collector-Key' = $env:HF811_SERVER_API_KEY }
    $id = [guid]::NewGuid().ToString()
    $event = @{
        event_id = $id; device_id = 'LINE-TEST-HF811'; barcode = 'PCB-X';
        raw_hex = '5043422d58'; source_address = '127.0.0.1';
        received_at_utc = '2026-11-06T08:00:00.000+00:00';
        is_duplicate = $false; is_no_read = $false
    }
    $payload = @{ collector_id = 'CI-RECEIVER'; events = @($event) } | ConvertTo-Json -Depth 6
    $receipt = Invoke-RestMethod -Uri $url -Method Post -Headers $headers -Body $payload -ContentType 'application/json'
    if (@($receipt.acknowledged_event_ids).Count -ne 1) { throw 'Server did not ACK a valid event' }
    $receipt2 = Invoke-RestMethod -Uri $url -Method Post -Headers $headers -Body $payload -ContentType 'application/json'
    if (@($receipt2.acknowledged_event_ids).Count -ne 1) { throw 'Server did not ACK exact replay' }
    $event.barcode = 'PCB-CHANGED'
    $changed = @{ collector_id = 'CI-RECEIVER'; events = @($event) } | ConvertTo-Json -Depth 6
    $conflict = Invoke-RestMethod -Uri $url -Method Post -Headers $headers -Body $changed -ContentType 'application/json'
    if (@($conflict.rejected).Count -ne 1) { throw 'Conflicting event_id was not rejected' }

    $summary = Invoke-RestMethod -Uri 'http://127.0.0.1:18090/api/v1/collector/stats' -Headers $headers
    if ($summary.total -ne 4) { throw "Expected 4 unique events, got $($summary.total)" }
    Write-Host "RECEIVER E2E PASS: 4 unique events, valid/replay ACK, conflict rejected, unauthenticated request denied." -ForegroundColor Green
}
finally {
    if ($server -and -not $server.HasExited) { Stop-Process -Id $server.Id -Force }
    $env:HF811_SERVER_API_KEY = $null
    $env:HF811_SYNC_API_KEY = $null
    $env:HF811_RECEIVER_DB = $null
}
