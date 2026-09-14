<#
.SYNOPSIS
    Installs NetTrace for the current user. No administrator rights, no UAC prompt.

.DESCRIPTION
    Copies the published build to %LocalAppData%\Programs\NetTrace, adds a Start Menu shortcut,
    and registers an uninstall entry under HKCU so the app appears in Settings > Apps.

    Everything written here is per-user. Nothing touches HKLM, Program Files, or system settings.
#>
[CmdletBinding()]
param(
    [string] $SourcePath = (Join-Path $PSScriptRoot '..\artifacts\portable'),
    [switch] $Autostart
)

$ErrorActionPreference = 'Stop'

if (-not (Test-Path $SourcePath)) {
    throw "Nothing to install at $SourcePath. Run publish-portable.ps1 first."
}

$target = Join-Path $env:LOCALAPPDATA 'Programs\NetTrace'
$exe = Join-Path $target 'NetTrace.exe'

Write-Host "Installing to $target" -ForegroundColor Cyan
New-Item -ItemType Directory -Force -Path $target | Out-Null
Copy-Item (Join-Path $SourcePath '*') $target -Recurse -Force

# Start Menu shortcut
$shortcut = Join-Path $env:APPDATA 'Microsoft\Windows\Start Menu\Programs\NetTrace.lnk'
$shell = New-Object -ComObject WScript.Shell
$link = $shell.CreateShortcut($shortcut)
$link.TargetPath = $exe
$link.WorkingDirectory = $target
$link.Description = 'Track which applications use your bandwidth'
$link.IconLocation = "$exe,0"
$link.Save()

# Uninstall entry, per-user
$uninstallKey = 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Uninstall\NetTrace'
New-Item -Path $uninstallKey -Force | Out-Null
$version = (Get-Item $exe).VersionInfo.ProductVersion
Set-ItemProperty -Path $uninstallKey -Name 'DisplayName'     -Value 'NetTrace'
Set-ItemProperty -Path $uninstallKey -Name 'DisplayIcon'     -Value "$exe,0"
Set-ItemProperty -Path $uninstallKey -Name 'DisplayVersion'  -Value ($version ?? '1.0.0.0')
Set-ItemProperty -Path $uninstallKey -Name 'Publisher'       -Value 'Lance du Plessis'
Set-ItemProperty -Path $uninstallKey -Name 'InstallLocation' -Value $target
Set-ItemProperty -Path $uninstallKey -Name 'NoModify'        -Value 1 -Type DWord
Set-ItemProperty -Path $uninstallKey -Name 'NoRepair'        -Value 1 -Type DWord
Set-ItemProperty -Path $uninstallKey -Name 'UninstallString' `
    -Value "powershell -ExecutionPolicy Bypass -File `"$target\uninstall-peruser.ps1`""

Copy-Item (Join-Path $PSScriptRoot 'uninstall-peruser.ps1') $target -Force

if ($Autostart) {
    Set-ItemProperty -Path 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Run' `
        -Name 'NetTrace' -Value "`"$exe`" --minimised"
    Write-Host 'Autostart enabled for this user.' -ForegroundColor Green
}

Write-Host "Installed. Launch it from the Start Menu, or run $exe" -ForegroundColor Green
