<#
.SYNOPSIS
  Builds BitaxeTuner: runs tests, publishes a self-contained single-file exe and creates the setup (Inno Setup).
.EXAMPLE
  .\build.ps1                 # version from Directory.Build.props
  .\build.ps1 -Version 0.2.0
  .\build.ps1 -SkipInstaller  # publish + portable zip + server packages, no setups
  .\build.ps1 -SkipServer     # desktop only

  Server packages: BitaxeTuner-Server-<v>-linux-arm64/-arm/-x64.tar.gz (Raspberry Pi / Linux, install.sh + systemd)
  and BitaxeTuner-Server-Setup-<v>.exe (Windows service). Docker images are built by the release workflow.
#>
param(
    [string]$Version,
    [switch]$SkipTests,
    [switch]$SkipInstaller,
    [switch]$SkipServer
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

# Lizenztexte gehören zu jeder Weitergabe (GPL-3.0 und Drittanbieter-Hinweise)
Copy-Item (Join-Path $root 'LICENSE') (Join-Path $publish 'LICENSE.txt')
Copy-Item (Join-Path $root 'THIRD-PARTY-NOTICES.txt') $publish
$zip = Join-Path $artifacts "BitaxeTuner-$Version-portable-win-x64.zip"
if (Test-Path $zip) { Remove-Item $zip }
Compress-Archive -Path (Join-Path $publish '*') -DestinationPath $zip
Write-Host "Portable: $zip" -ForegroundColor Green

# ---------- Server: Raspberry Pi / Linux (self-contained, install.sh + systemd) ----------
$serverProject = Join-Path $root 'src\BitaxeTuner.Server\BitaxeTuner.Server.csproj'
function Write-Lf([string]$path, [string]$text) {
    # Linux-Dateien immer mit LF und ohne BOM (sonst scheitert /bin/sh bzw. systemd)
    [IO.File]::WriteAllText($path, $text.Replace("`r`n", "`n"), (New-Object Text.UTF8Encoding $false))
}
if (-not $SkipServer) {
    foreach ($rid in 'linux-arm64', 'linux-arm', 'linux-x64') {
        $stage = Join-Path $artifacts "server-$rid"
        $pkg = Join-Path $stage 'bitaxetuner-server'
        if (Test-Path $stage) { Remove-Item $stage -Recurse -Force }
        dotnet publish $serverProject -c Release -r $rid --self-contained true `
            -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:EnableCompressionInSingleFile=true `
            -p:DebugType=none -p:Version=$Version -o $pkg
        if ($LASTEXITCODE -ne 0) { throw "Server publish failed ($rid)." }
        Write-Lf (Join-Path $pkg 'install.sh') (Get-Content (Join-Path $root 'deploy\linux\install.sh') -Raw -Encoding UTF8)
        foreach ($f in 'bitaxetuner.service', 'bitaxetuner-reboot.path', 'bitaxetuner-reboot.service', 'bitaxetuner-firstboot.service', 'bitaxetuner-firstboot.sh') {
            Write-Lf (Join-Path $pkg $f) (Get-Content (Join-Path $root "deploy\linux\$f") -Raw -Encoding UTF8)
        }
        Write-Lf (Join-Path $pkg 'VERSION') "$Version`n"
        Write-Lf (Join-Path $pkg 'LICENSE') (Get-Content (Join-Path $root 'LICENSE') -Raw -Encoding UTF8)
        Write-Lf (Join-Path $pkg 'THIRD-PARTY-NOTICES.txt') (Get-Content (Join-Path $root 'THIRD-PARTY-NOTICES.txt') -Raw -Encoding UTF8)
        Write-Lf (Join-Path $pkg 'LIESMICH.txt') @"
BitaxeTuner-Server $Version ($rid)

Installieren / aktualisieren:   sudo ./install.sh
Oberfläche:                     http://<IP-dieses-Geräts>:8484/
Einrichtungs-Code:              sudo cat /var/lib/bitaxetuner/SETUP-CODE.txt
Protokoll:                      journalctl -u bitaxetuner -f
Entfernen (Daten bleiben):      sudo ./install.sh --uninstall

Anleitung: https://github.com/Elemirus1996/BitaxeTuner#247-betrieb
"@
        $tgz = Join-Path $artifacts "BitaxeTuner-Server-$Version-$rid.tar.gz"
        if (Test-Path $tgz) { Remove-Item $tgz }
        tar -czf $tgz -C $stage bitaxetuner-server
        if ($LASTEXITCODE -ne 0) { throw "tar failed ($rid)." }
        Write-Host "Server: $tgz" -ForegroundColor Green
    }

    # Windows-Dienst: Dateien für das Server-Setup
    $serverWin = Join-Path $artifacts 'publish-server-win'
    if (Test-Path $serverWin) { Remove-Item $serverWin -Recurse -Force }
    dotnet publish $serverProject -c Release -r win-x64 --self-contained true `
        -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:EnableCompressionInSingleFile=true `
        -p:DebugType=none -p:Version=$Version -o $serverWin
    if ($LASTEXITCODE -ne 0) { throw 'Server publish failed (win-x64).' }
}

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

if (-not $SkipServer) {
    & $iscc "/DMyAppVersion=$Version" (Join-Path $root 'installer\BitaxeTuner-Server.iss')
    if ($LASTEXITCODE -ne 0) { throw 'Inno Setup (server) failed.' }
    Write-Host "Server-Setup: $(Join-Path $artifacts "BitaxeTuner-Server-Setup-$Version.exe")" -ForegroundColor Green
}

# SHA-256 checksums for the in-app updater (in addition to the GitHub asset digest)
$sums = Get-ChildItem $artifacts -File | Where-Object { $_.Name -like "BitaxeTuner-*$Version*" } | ForEach-Object {
    "$((Get-FileHash $_.FullName -Algorithm SHA256).Hash.ToLowerInvariant())  $($_.Name)"
}
[IO.File]::WriteAllLines((Join-Path $artifacts 'SHA256SUMS.txt'), $sums)
Write-Host "Checksums: $(Join-Path $artifacts 'SHA256SUMS.txt')" -ForegroundColor Green
