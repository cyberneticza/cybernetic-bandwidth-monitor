<#
.SYNOPSIS
    Answers the one question the whole design rests on: does this machine hand per-application
    byte counts to an unpackaged process running as a standard user?

.DESCRIPTION
    Run this BEFORE investing in the build. If it prints application names and byte counts, the
    architecture holds. If it prints nothing, you are choosing between MSIX packaging (which
    costs one elevated step to trust the signing certificate) and dropping to connections-only
    attribution — and it is much better to know that now.

    Requires the .NET 10 SDK. Creates and deletes a scratch console project in $env:TEMP.
#>
$ErrorActionPreference = 'Stop'
$scratch = Join-Path $env:TEMP ("ndu-probe-" + [guid]::NewGuid().ToString('N'))

New-Item -ItemType Directory -Path $scratch | Out-Null
Push-Location $scratch
try {
    & dotnet new console -o . --force | Out-Null

    @'
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <OutputType>Exe</OutputType>
    <TargetFramework>net10.0-windows10.0.19041.0</TargetFramework>
    <Nullable>enable</Nullable>
  </PropertyGroup>
</Project>
'@ | Set-Content 'ndu-probe.csproj' -Encoding UTF8
    Remove-Item *.csproj -Exclude 'ndu-probe.csproj' -Force -ErrorAction SilentlyContinue

    @'
using Windows.Networking.Connectivity;

var end = DateTimeOffset.UtcNow;
var start = end - TimeSpan.FromHours(6);
var states = new NetworkUsageStates { Roaming = TriStates.DoNotCare, Shared = TriStates.DoNotCare };

int total = 0;
foreach (var profile in NetworkInformation.GetConnectionProfiles())
{
    var usage = await profile.GetAttributedNetworkUsageAsync(start, end, states);
    foreach (var u in usage.OrderByDescending(u => u.BytesReceived).Take(15))
    {
        Console.WriteLine($"{u.AttributionName,-42} down {u.BytesReceived,14:N0}  up {u.BytesSent,14:N0}");
        total++;
    }
}

Console.WriteLine();
Console.WriteLine(total > 0
    ? $"OK - attribution works here ({total} entries in the last six hours)."
    : "EMPTY - no attributed usage returned. Either the machine was idle, or attribution is blocked.");
'@ | Set-Content 'Program.cs' -Encoding UTF8

    & dotnet run
}
finally {
    Pop-Location
    Remove-Item $scratch -Recurse -Force -ErrorAction SilentlyContinue
}
