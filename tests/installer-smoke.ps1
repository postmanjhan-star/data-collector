[CmdletBinding()]
param(
    [string]$InstallerPath = (Join-Path $PSScriptRoot '..\dist\installer\HF811Collector-win-x64-Setup.exe')
)
$ErrorActionPreference = 'Stop'

# This script installs and uninstalls the application. Run it ONLY on an ephemeral CI runner.
if ($env:GITHUB_ACTIONS -ne 'true') {
    throw 'Installer smoke test is restricted to disposable GitHub Actions runners.'
}

$installer = (Resolve-Path -LiteralPath $InstallerPath -ErrorAction Stop).Path
$appDir = Join-Path $env:LOCALAPPDATA 'Programs\HF811Collector'
$dataDir = Join-Path $env:LOCALAPPDATA 'HF811Collector'
$groupDir = Join-Path $env:APPDATA 'Microsoft\Windows\Start Menu\Programs\HF811 Collector'
$logFile = Join-Path $env:RUNNER_TEMP 'HF811Collector-install-smoke.log'
$uninstall = Join-Path $appDir 'unins000.exe'

if ((Test-Path -LiteralPath $appDir) -or (Test-Path -LiteralPath $dataDir)) {
    throw 'Refusing to overwrite an existing HF811 Collector installation or data directory.'
}

try {
    Write-Host "Installing $installer" -ForegroundColor Cyan
    # Setup.exe is a GUI-subsystem executable: direct invocation can return before it exits
    # and leave $LASTEXITCODE unset. Start-Process -Wait obtains the real exit code.
    $setup = Start-Process -FilePath $installer -ArgumentList @(
        '/VERYSILENT', '/SUPPRESSMSGBOXES', '/NORESTART',
        '/TASKS=!startup', "/LOG=$logFile"
    ) -Wait -PassThru
    if ($setup.ExitCode -ne 0) {
        throw "Installer exit code $($setup.ExitCode). Review $logFile"
    }

    $required = @(
        (Join-Path $appDir 'HF811Collector.exe'),
        (Join-Path $dataDir 'config.json'),
        (Join-Path $groupDir '01 啟動 Collector.lnk'),
        (Join-Path $groupDir '02 啟動本機模擬掃描器.lnk'),
        (Join-Path $groupDir '03 編輯 HF811 設定.lnk'),
        (Join-Path $groupDir '04 掃描資料與日誌資料夾.lnk'),
        (Join-Path $groupDir '05 Collector 狀態 API.lnk')
    )
    foreach ($path in $required) {
        if (-not (Test-Path -LiteralPath $path -PathType Leaf)) {
            throw "Required installed file or shortcut missing: $path"
        }
        Write-Host "Verified $path"
    }
    if (-not (Test-Path -LiteralPath $uninstall -PathType Leaf)) {
        throw "Uninstaller not found: $uninstall"
    }

    Write-Host 'Testing installed executable...' -ForegroundColor Cyan
    & (Join-Path $appDir 'HF811Collector.exe') selftest
    if ($LASTEXITCODE -ne 0) { throw "Installed executable self-test exit code $LASTEXITCODE" }

    Write-Host 'INSTALLER SMOKE TEST PASS: setup, shortcuts, config, self-test.' -ForegroundColor Green
}
finally {
    if (Test-Path -LiteralPath $uninstall -PathType Leaf) {
        Write-Host 'Uninstalling smoke-test installation...' -ForegroundColor Cyan
        $uninstallProcess = Start-Process -FilePath $uninstall -ArgumentList @(
            '/VERYSILENT', '/SUPPRESSMSGBOXES', '/NORESTART'
        ) -Wait -PassThru
        if ($uninstallProcess.ExitCode -ne 0) {
            Write-Warning "Uninstaller exit code $($uninstallProcess.ExitCode)"
        }
    }
}
