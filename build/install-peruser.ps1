<#
.SYNOPSIS
    Installs Cybernetic Bandwidth Monitor for the current user. No administrator rights, no UAC prompt.

.DESCRIPTION
    Copies the published build to %LocalAppData%\Programs\CyberneticBandwidth, adds a Start Menu shortcut,
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

$target = Join-Path $env:LOCALAPPDATA 'Programs\CyberneticBandwidth'
$exe = Join-Path $target 'CyberneticBandwidth.exe'

# The app was called NetTrace. Its service needs administrator approval to remove and would go on
# recording beside this copy, so it has to go first, from the NetTrace that installed it.
if (Get-Service -Name 'NetTrace' -ErrorAction SilentlyContinue) {
    throw 'The NetTrace Windows service from before the rename is installed. Remove it first: in NetTrace, ' +
        'open Settings > Running in the background and select Remove service. Nothing was changed.'
}

# A per-user NetTrace install is replaced. Its history in %LocalAppData%\NetTrace and its sign-in
# entry stay: the renamed app moves both over to its own name on its first start.
$legacy = Join-Path $env:LOCALAPPDATA 'Programs\NetTrace'
if (Test-Path $legacy) {
    Write-Host "Removing the NetTrace install in $legacy" -ForegroundColor Cyan
    Get-Process -Name 'NetTrace' -ErrorAction SilentlyContinue | Stop-Process -Force
    Start-Sleep -Milliseconds 500
    Remove-Item (Join-Path $env:APPDATA 'Microsoft\Windows\Start Menu\Programs\NetTrace.lnk') -Force -ErrorAction SilentlyContinue
    Remove-Item 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Uninstall\NetTrace' -Recurse -Force -ErrorAction SilentlyContinue
    Remove-Item $legacy -Recurse -Force
}

Write-Host "Installing to $target" -ForegroundColor Cyan
New-Item -ItemType Directory -Force -Path $target | Out-Null
Copy-Item (Join-Path $SourcePath '*') $target -Recurse -Force

# Start Menu shortcut
$shortcut = Join-Path $env:APPDATA 'Microsoft\Windows\Start Menu\Programs\CyberneticBandwidth.lnk'
$shell = New-Object -ComObject WScript.Shell
$link = $shell.CreateShortcut($shortcut)
$link.TargetPath = $exe
$link.WorkingDirectory = $target
$link.Description = 'Track which applications use your bandwidth'
$link.IconLocation = "$exe,0"
$link.Save()

# Uninstall entry, per-user
$uninstallKey = 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Uninstall\CyberneticBandwidth'
New-Item -Path $uninstallKey -Force | Out-Null
$version = (Get-Item $exe).VersionInfo.ProductVersion
Set-ItemProperty -Path $uninstallKey -Name 'DisplayName'     -Value 'Cybernetic Bandwidth Monitor'
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
        -Name 'CyberneticBandwidth' -Value "`"$exe`" --minimised"
    Write-Host 'Autostart enabled for this user.' -ForegroundColor Green
}

Write-Host "Installed. Launch it from the Start Menu, or run $exe" -ForegroundColor Green
