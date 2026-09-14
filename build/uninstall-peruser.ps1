<#
.SYNOPSIS
    Removes Cybernetic Bandwidth Monitor for the current user.
.PARAMETER KeepData
    Leaves the collected history in %LocalAppData%\CyberneticBandwidth in place.
#>
[CmdletBinding()]
param([switch] $KeepData)

# The service is machine-wide and needs administrator approval to remove. Removing it from Settings
# also copies its history back to this user, so that has to happen before the program goes.
if (Get-Service -Name 'CyberneticBandwidth' -ErrorAction SilentlyContinue) {
    Write-Host 'The Cybernetic Bandwidth Monitor Windows service is installed. Remove it first: in Cybernetic Bandwidth Monitor, open' -ForegroundColor Yellow
    Write-Host 'Settings > Running in the background and select Remove service. Nothing was changed.' -ForegroundColor Yellow
    exit 1
}

$ErrorActionPreference = 'SilentlyContinue'

Get-Process -Name 'CyberneticBandwidth' | Stop-Process -Force
Start-Sleep -Milliseconds 500

Remove-Item (Join-Path $env:APPDATA 'Microsoft\Windows\Start Menu\Programs\CyberneticBandwidth.lnk') -Force
Unregister-ScheduledTask -TaskName "Cybernetic Bandwidth Monitor at sign-in ($env:USERNAME)" -Confirm:$false
Remove-ItemProperty -Path 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Run' -Name 'CyberneticBandwidth' -Force
Remove-Item -Path 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Uninstall\CyberneticBandwidth' -Recurse -Force
Remove-Item (Join-Path $env:LOCALAPPDATA 'Programs\CyberneticBandwidth') -Recurse -Force

if (-not $KeepData) {
    Remove-Item (Join-Path $env:LOCALAPPDATA 'CyberneticBandwidth') -Recurse -Force
    Write-Host 'Cybernetic Bandwidth Monitor and its collected history have been removed.'
} else {
    Write-Host 'Cybernetic Bandwidth Monitor removed. Your history is still in %LocalAppData%\CyberneticBandwidth.'
}
