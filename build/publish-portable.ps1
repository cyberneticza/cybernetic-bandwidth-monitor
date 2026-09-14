<#
.SYNOPSIS
    Publishes NetTrace as a self-contained, single-file x64 build.

.DESCRIPTION
    Self-contained by default so the target machine does not need the .NET 10 Desktop Runtime
    installed — installing a runtime needs administrator rights, which is the one thing this
    app must never require. Costs about 80 MB.

    If your machines already have the runtime, pass -FrameworkDependent for a ~5 MB output.

    Trimming is deliberately off: WPF and CsWinRT both rely on reflection the trimmer cannot see.
#>
[CmdletBinding()]
param(
    [string] $Configuration = 'Release',
    [string] $OutputPath = (Join-Path $PSScriptRoot '..\artifacts\portable'),
    [switch] $FrameworkDependent
)

$ErrorActionPreference = 'Stop'
$project = Join-Path $PSScriptRoot '..\src\NetTrace.App\NetTrace.App.csproj'

$arguments = @(
    'publish', $project,
    '-c', $Configuration,
    '-r', 'win-x64',
    '-o', $OutputPath,
    '-p:PublishSingleFile=true',
    '-p:IncludeNativeLibrariesForSelfExtract=true',
    '-p:EnableCompressionInSingleFile=true',
    '-p:PublishReadyToRun=true'
)

$arguments += $FrameworkDependent ? '--self-contained:false' : '--self-contained:true'

Write-Host "Publishing to $OutputPath" -ForegroundColor Cyan
& dotnet @arguments
if ($LASTEXITCODE -ne 0) { throw "dotnet publish failed with exit code $LASTEXITCODE." }

Write-Host 'Done.' -ForegroundColor Green
Get-ChildItem $OutputPath | Select-Object Name, @{ n = 'Size'; e = { '{0:N1} MB' -f ($_.Length / 1MB) } }
