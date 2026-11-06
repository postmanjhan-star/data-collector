[CmdletBinding()]
param([switch]$SkipInstaller)
$ErrorActionPreference = 'Stop'
Set-Location $PSScriptRoot
if (-not (Get-Command dotnet -ErrorAction SilentlyContinue)) {
    throw 'Build PC requires .NET 10 SDK. Target Windows 10 laptop requires NO Python or .NET runtime.'
}
$dest = Join-Path $PSScriptRoot 'dist\win-x64'
Write-Host 'Publishing self-contained win-x64...' -ForegroundColor Cyan
& dotnet publish .\src\HF811Collector.csproj -c Release -r win-x64 --self-contained true `
    -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true `
    -p:PublishTrimmed=false -p:DebugType=none -o $dest
if ($LASTEXITCODE -ne 0) { throw 'dotnet publish failed for win-x64' }
$exe = Join-Path $dest 'HF811Collector.exe'
if (-not (Test-Path $exe)) { throw 'Published HF811Collector.exe not found' }
Write-Host 'Running C# self-test...' -ForegroundColor Cyan
& $exe selftest
if ($LASTEXITCODE -ne 0) { throw 'C# self-test failed' }
$desktopDest = Join-Path $PSScriptRoot 'dist\win-x64-desktop'
Write-Host 'Publishing self-contained Windows Forms tray application...' -ForegroundColor Cyan
& dotnet publish .\desktop\HF811Collector.Desktop.csproj -c Release -r win-x64 --self-contained true `
    -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true `
    -p:PublishTrimmed=false -p:DebugType=none -o $desktopDest
if ($LASTEXITCODE -ne 0) { throw 'dotnet publish failed for Desktop GUI' }
$desktopExe = Join-Path $desktopDest 'HF811Collector.Desktop.exe'
if (-not (Test-Path $desktopExe)) { throw 'Desktop application executable not found' }
Copy-Item -LiteralPath .\src\config.json -Destination (Join-Path $desktopDest 'config.default.json') -Force
Write-Host 'Running installed-asset GUI self-test...' -ForegroundColor Cyan
$desktopSmoke = Start-Process -FilePath $desktopExe -ArgumentList '--selftest' -Wait -PassThru
if ($desktopSmoke.ExitCode -ne 0) { throw "GUI self-test failed: $($desktopSmoke.ExitCode)" }
if ($SkipInstaller) { Write-Host "Published $exe and $desktopExe. Installer skipped."; exit 0 }
$iscc = Get-Command ISCC.exe -ErrorAction SilentlyContinue | Select-Object -First 1 -ExpandProperty Source
if (-not $iscc) {
    $candidates = @(
      "$env:LOCALAPPDATA\Programs\Inno Setup 7\ISCC.exe",
      "$env:LOCALAPPDATA\Programs\Inno Setup 6\ISCC.exe",
      "$env:LOCALAPPDATA\Inno Setup 7\ISCC.exe",
      "$env:LOCALAPPDATA\Inno Setup 6\ISCC.exe",
      "${env:ProgramFiles(x86)}\Inno Setup 7\ISCC.exe",
      "$env:ProgramFiles\Inno Setup 7\ISCC.exe",
      "${env:ProgramFiles(x86)}\Inno Setup 6\ISCC.exe",
      "$env:ProgramFiles\Inno Setup 6\ISCC.exe"
    )
    $iscc = $candidates | Where-Object { $_ -and (Test-Path $_) } | Select-Object -First 1
}
if (-not $iscc) { throw 'ISCC.exe not found. Update the candidate locations in build.ps1 or run ISCC.exe manually.' }
Write-Host 'Building the Windows 10 x64 setup file...' -ForegroundColor Cyan
& $iscc .\installer\HF811Collector.iss
if ($LASTEXITCODE -ne 0) { throw 'Inno Setup failed' }
$installer = Join-Path $PSScriptRoot 'dist\installer\HF811Collector-win-x64-Setup.exe'
if (-not (Test-Path $installer)) { throw 'Installer output not found' }
$hash = (Get-FileHash -Algorithm SHA256 $installer).Hash
"SHA256 $hash  HF811Collector-win-x64-Setup.exe" | Set-Content -Encoding Ascii (Join-Path $PSScriptRoot 'dist\installer\SHA256SUMS.txt')
Write-Host "Installer: $installer" -ForegroundColor Green
Write-Host "SHA256: $hash"
