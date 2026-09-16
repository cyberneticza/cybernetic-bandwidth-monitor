<#
.SYNOPSIS
    Builds the one-click installer: CyberneticBandwidthMonitor-win-Setup.exe.

.DESCRIPTION
    Publishes (unless -SkipPublish), then packs artifacts\portable with Velopack's vpk into
    artifacts\releases. Share the Setup.exe: it installs for the current user with no UAC prompt,
    and the Setup.exe of a newer version updates that install in place.

    artifacts\releases is kept between runs: vpk makes a small delta update from the previous
    release it finds there. The version is the published exe's, from VersionPrefix.

    For updates from inside the app, copy the release files to the folder or web address named in
    Updates:Source in appsettings.json, or pass it as -PublishTo when it is a folder.
#>
[CmdletBinding()]
param(
    [string] $OutputPath = (Join-Path $PSScriptRoot '..\artifacts\releases'),
    [string] $PublishTo,
    [switch] $SkipPublish
)

$ErrorActionPreference = 'Stop'
$root = Join-Path $PSScriptRoot '..'
$portable = Join-Path $root 'artifacts\portable'
$exe = Join-Path $portable 'CyberneticBandwidth.exe'

if (-not $SkipPublish) {
    & (Join-Path $PSScriptRoot 'publish-portable.ps1')
}
if (-not (Test-Path $exe)) {
    throw "No published build at $portable. Run publish-portable.ps1 first, or drop -SkipPublish."
}

$version = (Get-Item $exe).VersionInfo.ProductVersion -replace '\+.*$', ''

Push-Location $root
try {
    # vpk is a local tool, pinned in dotnet-tools.json to the Velopack package's version.
    & dotnet tool restore
    if ($LASTEXITCODE -ne 0) { throw "dotnet tool restore failed with exit code $LASTEXITCODE." }

    # The package id must stay CyberneticBandwidthMonitor: it names the install folder, which
    # uninstalling deletes (see Installer.PackId).
    $arguments = @(
        'vpk', 'pack',
        '--packId', 'CyberneticBandwidthMonitor',
        '--packVersion', $version,
        '--packDir', $portable,
        '--mainExe', 'CyberneticBandwidth.exe',
        '--packTitle', 'Cybernetic Bandwidth Monitor',
        '--packAuthors', 'Lance du Plessis',
        '--icon', (Join-Path $root 'src\CyberneticBandwidth.App\Assets\CyberneticBandwidth.ico'),
        '--shortcuts', 'StartMenuRoot,Desktop',
        '--noPortable',
        '--outputDir', $OutputPath
    )
    Write-Host "Packing Cybernetic Bandwidth Monitor $version into $OutputPath" -ForegroundColor Cyan
    & dotnet @arguments
    if ($LASTEXITCODE -ne 0) { throw "vpk pack failed with exit code $LASTEXITCODE." }

    if ($PublishTo) {
        & dotnet vpk upload local --path $PublishTo --outputDir $OutputPath
        if ($LASTEXITCODE -ne 0) { throw "vpk upload failed with exit code $LASTEXITCODE." }
        Write-Host "Copied the release to $PublishTo" -ForegroundColor Green
    }
}
finally {
    Pop-Location
}

Write-Host "Share $((Resolve-Path (Join-Path $OutputPath 'CyberneticBandwidthMonitor-win-Setup.exe')).Path)" -ForegroundColor Green
