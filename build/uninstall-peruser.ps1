<#
.SYNOPSIS
    Removes NetTrace for the current user.
.PARAMETER KeepData
    Leaves the collected history in %LocalAppData%\NetTrace in place.
#>
[CmdletBinding()]
param([switch] $KeepData)

# The service is machine-wide and needs administrator approval to remove. Removing it from Settings
# also copies its history back to this user, so that has to happen before the program goes.
if (Get-Service -Name 'NetTrace' -ErrorAction SilentlyContinue) {
    Write-Host 'The NetTrace Windows service is installed. Remove it first: in NetTrace, open' -ForegroundColor Yellow
    Write-Host 'Settings > Running in the background and select Remove service. Nothing was changed.' -ForegroundColor Yellow
    exit 1
}

$ErrorActionPreference = 'SilentlyContinue'

Get-Process -Name 'NetTrace' | Stop-Process -Force
Start-Sleep -Milliseconds 500

Remove-Item (Join-Path $env:APPDATA 'Microsoft\Windows\Start Menu\Programs\NetTrace.lnk') -Force
Unregister-ScheduledTask -TaskName "NetTrace at sign-in ($env:USERNAME)" -Confirm:$false
Remove-ItemProperty -Path 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Run' -Name 'NetTrace' -Force
Remove-Item -Path 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Uninstall\NetTrace' -Recurse -Force
Remove-Item (Join-Path $env:LOCALAPPDATA 'Programs\NetTrace') -Recurse -Force

if (-not $KeepData) {
    Remove-Item (Join-Path $env:LOCALAPPDATA 'NetTrace') -Recurse -Force
    Write-Host 'NetTrace and its collected history have been removed.'
} else {
    Write-Host 'NetTrace removed. Your history is still in %LocalAppData%\NetTrace.'
}
