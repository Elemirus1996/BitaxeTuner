<#
.SYNOPSIS
  Builds BitaxeTuner: runs tests, publishes a self-contained single-file exe and creates the setup (Inno Setup).
.EXAMPLE
  .\build.ps1                 # version from Directory.Build.props
  .\build.ps1 -Version 0.2.0
  .\build.ps1 -SkipInstaller  # publish + portable zip only
#>
param(
    [string]$Version,
    [switch]$SkipTests,
    [switch]$SkipInstaller
)

$ErrorActionPreference = 'Stop'
$root = $PSScriptRoot
$artifacts = Join-Path $root 'artifacts'
$publish = Join-Path $artifacts 'publish'

if (-not $Version) {
    $Version = ([xml](Get-Content (Join-Path $root 'Directory.Build.props'))).Project.PropertyGroup.Version
}
$Version = $Version.TrimStart('v')
Write-Host "== BitaxeTuner $Version ==" -ForegroundColor Cyan

if (-not $SkipTests) {
    dotnet test (Join-Path $root 'BitaxeTuner.sln') -c Release
    if ($LASTEXITCODE -ne 0) { throw 'Tests failed.' }
}

if (Test-Path $publish) { Remove-Item $publish -Recurse -Force }
dotnet publish (Join-Path $root 'src\BitaxeTuner.App\BitaxeTuner.App.csproj') `
    -c Release -r win-x64 --self-contained true `
    -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:EnableCompressionInSingleFile=true `
    -p:DebugType=none -p:Version=$Version -o $publish
if ($LASTEXITCODE -ne 0) { throw 'Publish failed.' }

$zip = Join-Path $artifacts "BitaxeTuner-$Version-portable-win-x64.zip"
if (Test-Path $zip) { Remove-Item $zip }
Compress-Archive -Path (Join-Path $publish '*') -DestinationPath $zip
Write-Host "Portable: $zip" -ForegroundColor Green

if ($SkipInstaller) { return }

function Find-Iscc {
    $cmd = Get-Command iscc.exe -ErrorAction SilentlyContinue
    if ($cmd) { return $cmd.Source }
    $candidates = @(
        "${env:ProgramFiles(x86)}\Inno Setup 6\ISCC.exe",
        "$env:ProgramFiles\Inno Setup 6\ISCC.exe",
        "$env:LOCALAPPDATA\Programs\Inno Setup 6\ISCC.exe"
    )
    # Installations in other folders/drives: look up the uninstall registry entries.
    $keys = 'HKLM:\Software\Microsoft\Windows\CurrentVersion\Uninstall\*',
            'HKLM:\Software\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall\*',
            'HKCU:\Software\Microsoft\Windows\CurrentVersion\Uninstall\*'
    $candidates += Get-ItemProperty $keys -ErrorAction SilentlyContinue |
        Where-Object { $_.DisplayName -like 'Inno Setup*' -and $_.InstallLocation } |
        ForEach-Object { Join-Path $_.InstallLocation 'ISCC.exe' }
    return $candidates | Where-Object { Test-Path $_ } | Select-Object -First 1
}

$iscc = Find-Iscc
if (-not $iscc) {
    Write-Host 'Inno Setup not found - installing via winget ...' -ForegroundColor Yellow
    winget install --id JRSoftware.InnoSetup -e --silent --accept-package-agreements --accept-source-agreements
    $iscc = Find-Iscc
    if (-not $iscc) { throw 'Inno Setup (ISCC.exe) not found. Install it from https://jrsoftware.org/isdl.php' }
}

& $iscc "/DMyAppVersion=$Version" (Join-Path $root 'installer\BitaxeTuner.iss')
if ($LASTEXITCODE -ne 0) { throw 'Inno Setup failed.' }
Write-Host "Setup: $(Join-Path $artifacts "BitaxeTuner-Setup-$Version.exe")" -ForegroundColor Green
