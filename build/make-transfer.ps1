<#
.SYNOPSIS
    Builds the transfer packages for putting Cybernetic Bandwidth Monitor on another device.

.DESCRIPTION
    Creates artifacts\transfer with one folder per way of putting Cybernetic Bandwidth Monitor on a device, each holding
    every file that option needs plus its install guide as a PDF:

      Option1-Portable   CyberneticBandwidth.exe and appsettings.json to copy anywhere and run.
      Option2-Install    app\ with the same files, plus the per-user install and uninstall scripts.

    The guides are HTML templates in build\transfer, printed to PDF with Microsoft Edge in headless
    mode. Publishes first unless -SkipPublish is given, so the packages always match the guides.
#>
[CmdletBinding()]
param(
    [string] $OutputPath = (Join-Path $PSScriptRoot '..\artifacts\transfer'),
    [switch] $SkipPublish
)

$ErrorActionPreference = 'Stop'
$portable = Join-Path $PSScriptRoot '..\artifacts\portable'
$templates = Join-Path $PSScriptRoot 'transfer'

if (-not $SkipPublish) {
    & (Join-Path $PSScriptRoot 'publish-portable.ps1')
}
if (-not (Test-Path (Join-Path $portable 'CyberneticBandwidth.exe'))) {
    throw "No published build at $portable. Run publish-portable.ps1 first, or drop -SkipPublish."
}

$edge = @(
    "${env:ProgramFiles(x86)}\Microsoft\Edge\Application\msedge.exe",
    "$env:ProgramFiles\Microsoft\Edge\Application\msedge.exe"
) | Where-Object { Test-Path $_ } | Select-Object -First 1
if (-not $edge) { throw 'Microsoft Edge was not found; it is needed to print the guides to PDF.' }

$version = (Get-Item (Join-Path $portable 'CyberneticBandwidth.exe')).VersionInfo.ProductVersion -replace '\+.*$', ''
$date = Get-Date -Format 'd MMMM yyyy'

# Start clean so files dropped from a package do not linger in it.
if (Test-Path $OutputPath) { Remove-Item $OutputPath -Recurse -Force }
$option1 = New-Item -ItemType Directory -Force -Path (Join-Path $OutputPath 'Option1-Portable')
$option2 = New-Item -ItemType Directory -Force -Path (Join-Path $OutputPath 'Option2-Install')
$option2App = New-Item -ItemType Directory -Force -Path (Join-Path $option2 'app')

# The exe and its settings are all the app needs; the .pdb debugging symbols stay behind.
foreach ($folder in $option1, $option2App) {
    Copy-Item (Join-Path $portable 'CyberneticBandwidth.exe') $folder
    Copy-Item (Join-Path $portable 'appsettings.json') $folder
}
Copy-Item (Join-Path $PSScriptRoot 'install-peruser.ps1') $option2
Copy-Item (Join-Path $PSScriptRoot 'uninstall-peruser.ps1') $option2

$css = Get-Content (Join-Path $templates 'guide.css') -Raw -Encoding UTF8
$pwsh = Get-Content (Join-Path $templates 'pwsh-section.html') -Raw -Encoding UTF8
$work = New-Item -ItemType Directory -Force -Path (Join-Path ([IO.Path]::GetTempPath()) ("cyberneticbandwidth-guides-" + [guid]::NewGuid().ToString('N')))

try {
    $guides = @(
        @{ Template = 'option1.html'; Pdf = Join-Path $option1 'CyberneticBandwidth-Option1-Install-Guide.pdf' },
        @{ Template = 'option2.html'; Pdf = Join-Path $option2 'CyberneticBandwidth-Option2-Install-Guide.pdf' }
    )

    foreach ($guide in $guides) {
        $body = (Get-Content (Join-Path $templates $guide.Template) -Raw -Encoding UTF8).
            Replace('{{CSS}}', $css).Replace('{{PWSH}}', $pwsh).
            Replace('{{VERSION}}', $version).Replace('{{DATE}}', $date)
        $html = Join-Path $work $guide.Template
        Set-Content -Path $html -Encoding UTF8 -Value "<!doctype html><html lang=`"en`"><head><meta charset=`"utf-8`"></head><body>$body</body></html>"

        # A private profile keeps this from attaching to, or being blocked by, an Edge window already open.
        $edgeProfile = Join-Path $work "edge-$($guide.Template)"
        $arguments = @(
            '--headless=new', '--disable-gpu', '--no-first-run', '--no-pdf-header-footer',
            "--user-data-dir=`"$edgeProfile`"",
            "--print-to-pdf=`"$($guide.Pdf)`"",
            ([Uri] $html).AbsoluteUri
        )
        $process = Start-Process -FilePath $edge -ArgumentList $arguments -PassThru -WindowStyle Hidden

        # Headless Edge sometimes writes the PDF and then never exits, so wait for the file rather than
        # the process, then close whatever is left of this profile's Edge processes.
        $deadline = (Get-Date).AddSeconds(60)
        while (-not $process.HasExited -and (Get-Date) -lt $deadline) {
            if ((Test-Path $guide.Pdf) -and (Get-Item $guide.Pdf).Length -gt 0) { Start-Sleep -Seconds 2; break }
            Start-Sleep -Milliseconds 250
        }
        Get-CimInstance Win32_Process -Filter "Name = 'msedge.exe'" |
            Where-Object { $_.CommandLine -like "*$edgeProfile*" } |
            ForEach-Object { Stop-Process -Id $_.ProcessId -Force -ErrorAction SilentlyContinue }

        if (-not (Test-Path $guide.Pdf)) { throw "Edge did not produce $($guide.Pdf)." }
    }
}
finally {
    Remove-Item $work -Recurse -Force -ErrorAction SilentlyContinue
}

Write-Host "Transfer packages for Cybernetic Bandwidth Monitor $version are in $((Resolve-Path $OutputPath).Path)" -ForegroundColor Green
Get-ChildItem $OutputPath -Recurse -File |
    Select-Object @{ n = 'File'; e = { $_.FullName.Substring((Resolve-Path $OutputPath).Path.Length + 1) } },
                  @{ n = 'Size'; e = { '{0:N1} MB' -f ($_.Length / 1MB) } }
