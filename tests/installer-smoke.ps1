[CmdletBinding()]
param(
    [ValidateSet('CurrentUser', 'AllUsers')]
    [string]$InstallMode = 'CurrentUser',
    [string]$InstallerPath = (Join-Path $PSScriptRoot '..\dist\installer\HF811Collector-win-x64-Setup.exe')
)
$ErrorActionPreference = 'Stop'

# The test installs and uninstalls real programs. Never execute it on production laptops.
if ($env:GITHUB_ACTIONS -ne 'true') {
    throw 'Installer smoke tests are restricted to disposable GitHub Actions runners.'
}

$installer = (Resolve-Path -LiteralPath $InstallerPath -ErrorAction Stop).Path
$dataDir = Join-Path $env:LOCALAPPDATA 'HF811Collector'
$configPath = Join-Path $dataDir 'config.json'
$commonStartMenu = Join-Path $env:ProgramData 'Microsoft\Windows\Start Menu\Programs\HF811 Collector'
$userStartMenu = Join-Path $env:APPDATA 'Microsoft\Windows\Start Menu\Programs\HF811 Collector'
if ($InstallMode -eq 'AllUsers') {
    $appDir = Join-Path $env:ProgramFiles 'HF811Collector'
    $groupDir = $commonStartMenu
    $scope = '/ALLUSERS'
}
else {
    $appDir = Join-Path $env:LOCALAPPDATA 'Programs\HF811Collector'
    $groupDir = $userStartMenu
    $scope = '/CURRENTUSER'
}
$logFile = Join-Path $env:RUNNER_TEMP "HF811Collector-install-smoke-$InstallMode.log"
$exe = Join-Path $appDir 'HF811Collector.exe'
$uninstaller = Join-Path $appDir 'unins000.exe'

if (Test-Path -LiteralPath $appDir) {
    throw "Refusing to overwrite an existing installation: $appDir"
}
# The prior test mode intentionally leaves its user configuration behind.
$oldHash = if (Test-Path -LiteralPath $configPath -PathType Leaf) {
    (Get-FileHash -LiteralPath $configPath -Algorithm SHA256).Hash
} else { $null }

try {
    Write-Host "Installing $InstallMode scope: $installer" -ForegroundColor Cyan
    $setup = Start-Process -FilePath $installer -ArgumentList @(
        $scope, '/VERYSILENT', '/SUPPRESSMSGBOXES', '/NORESTART',
        '/TASKS=!startup', "/LOG=$logFile"
    ) -Wait -PassThru
    if ($setup.ExitCode -ne 0) {
        throw "Installer exited $($setup.ExitCode). Review $logFile"
    }

    $required = @(
        $exe,
        (Join-Path $appDir 'config.default.json'),
        (Join-Path $groupDir '01 啟動 Collector.lnk'),
        (Join-Path $groupDir '02 啟動本機模擬掃描器.lnk'),
        (Join-Path $groupDir '03 編輯 HF811 設定.lnk'),
        (Join-Path $groupDir '04 掃描資料與日誌資料夾.lnk')
    )
    foreach ($file in $required) {
        if (-not (Test-Path -LiteralPath $file -PathType Leaf)) {
            throw "Required installed file or shortcut missing: $file"
        }
        Write-Host "Verified $file"
    }
    # HTTP destinations are written as Internet shortcut .url files by Inno Setup.
    $apiShortcut = @(
        (Join-Path $groupDir '05 Collector 狀態 API.url'),
        (Join-Path $groupDir '05 Collector 狀態 API.lnk')
    ) | Where-Object { Test-Path -LiteralPath $_ -PathType Leaf } |
        Select-Object -First 1
    if (-not $apiShortcut) { throw "API shortcut missing under $groupDir" }
    Write-Host "Verified $apiShortcut"

    if (-not (Test-Path -LiteralPath $uninstaller -PathType Leaf)) {
        throw "Uninstaller missing: $uninstaller"
    }
    Write-Host "Testing first-run per-user configuration ($InstallMode)..." -ForegroundColor Cyan
    & $exe init-config
    if ($LASTEXITCODE -ne 0) { throw "init-config exited $LASTEXITCODE" }
    if (-not (Test-Path -LiteralPath $configPath -PathType Leaf)) {
        throw "Per-user config not initialized: $configPath"
    }
    if ($oldHash -and (Get-FileHash -LiteralPath $configPath -Algorithm SHA256).Hash -ne $oldHash) {
        throw 'An existing user configuration was overwritten.'
    }
    Write-Host "Verified per-user config: $configPath"

    & $exe selftest
    if ($LASTEXITCODE -ne 0) { throw "Installed executable self-test exited $LASTEXITCODE" }
    Write-Host "INSTALLER SMOKE TEST PASS: $InstallMode installation, shortcuts, config and self-test." -ForegroundColor Green
}
finally {
    if (Test-Path -LiteralPath $uninstaller -PathType Leaf) {
        Write-Host "Uninstalling $InstallMode smoke-test installation..." -ForegroundColor Cyan
        $uninstall = Start-Process -FilePath $uninstaller -ArgumentList @(
            '/VERYSILENT', '/SUPPRESSMSGBOXES', '/NORESTART'
        ) -Wait -PassThru
        if ($uninstall.ExitCode -ne 0) {
            Write-Warning "Uninstaller exit code $($uninstall.ExitCode)"
        }
        if (Test-Path -LiteralPath $exe -PathType Leaf) {
            throw "Uninstall did not remove $exe"
        }
    }
}
