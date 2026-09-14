# NetTrace

A per-user network usage tracker for Windows 10/11. Shows which applications are using your
bandwidth, keeps the history locally, and charts it over periods from a day to a year.

Runs as a standard user. No driver, no service, no UAC prompt, nothing written outside your
own profile.

---

## Before you build: run the probe

The entire design rests on one question — does this machine give per-application byte counts to
an unpackaged process running unelevated? Find out in two minutes:

```powershell
pwsh -File build\probe-ndu.ps1
```

If it prints application names and byte counts, the architecture holds and you can build.

If it prints `EMPTY` on a machine that has been online, attribution is blocked here. Your options
are MSIX packaging (which declares the `networkDataPlanProvisioning` capability, at the cost of
one elevated step to trust the signing certificate) or running with connections-only attribution.
Much better to discover that now than in week three.

## Build and install

```powershell
dotnet restore
dotnet build -c Release

# Publish a self-contained single file, then install it for the current user
pwsh -File build\publish-portable.ps1
pwsh -File build\install-peruser.ps1 -Autostart
```

Installs to `%LocalAppData%\Programs\NetTrace` with a Start Menu shortcut and a per-user uninstall
entry. `build\uninstall-peruser.ps1` reverses all of it; add `-KeepData` to leave your history.

Collected data lives in `%LocalAppData%\NetTrace` — `nettrace.db` plus seven days of rolling logs.

## How it measures, and what that costs you

Monitoring without administrator rights rules out almost everything a network monitor normally
does. Packet capture needs a driver. A WFP callout needs the kernel. ETW's kernel network provider
needs `SeSystemProfilePrivilege`. So NetTrace fuses three sources that a standard user *can* read:

| Source | Gives | Refresh |
|---|---|---|
| Windows Data Usage (NDU), via `ConnectionProfile.GetAttributedNetworkUsageAsync` | Bytes sent and received, attributed per application | ~1 minute |
| `GetExtendedTcpTable` / `GetExtendedUdpTable` | PID, protocol, local and remote endpoints, TCP state | 5 seconds |
| NIC counters | Machine-wide throughput for the live strip | 1 second |

Two consequences worth internalising before you extend this:

**Resolution is about a minute.** Anything finer would be invented. The UI never claims otherwise.

**Byte counts and sockets use different name spaces.** NDU attributes to applications; the
connection table is keyed by PID. They do not line up reliably, so the live view presents them as
two separate tables rather than joining them into one convincing-looking lie.

## Layout

```
src/NetTrace.Core    Sources, interop, sampler, domain models. No UI, no database.
src/NetTrace.Data    SQLite schema, repository, rollup and retention.
src/NetTrace.App     WPF shell, view models, theme.
build/               Publish, install, uninstall, probe.
```

`INetworkUsageSource` is the seam that matters. The sampler picks the highest-priority source that
reports itself available and knows nothing else about it, so adding an elevated ETW collector later
is a new class and one DI registration — nothing above it changes.

## Storage

Timestamps are Unix seconds, so bucket alignment is integer arithmetic (`t / 3600 * 3600`) that
SQLite indexes and groups on without parsing dates or guessing at time zones.

Raw minute buckets roll up to hours and then days:

| Granularity | Kept for | Serves |
|---|---|---|
| Minute | 7 days | 1 day |
| Hour | 120 days | 1 week through 1 month |
| Day | 3 years | 6 months, 1 year |

Every chart therefore touches at most a few thousand rows. Expect the database to settle under
about 50 MB on a busy machine.

Pruning cuts on the *parent* bucket's boundary, never mid-bucket. Deleting half an hour's worth of
minute rows would make the next promotion overwrite that hour with a partial sum.

Samples carry absolute totals for their bucket, never deltas, and writes upsert with
`DO UPDATE SET` rather than adding. That is what lets the sampler re-read the last three minutes on
every pass — NDU back-fills late, and re-reading is free correctness instead of a double count.

## Keeping it cheap

Three independent cadences, pooled buffers in the interop layer, one transaction per tick rather
than per row, WAL journaling, in-place merges into the app table instead of rebuilding it, and a
4x interval stretch once `GetLastInputInfo` says you have been away five minutes.

Only socket *transitions* are persisted. Writing the whole table every five seconds would add
roughly half a million rows a day and tell you nothing.

## Where to take it next

- **Elevated ETW collector.** `Microsoft-Windows-Kernel-Network` gives packet-accurate, sub-second,
  per-PID *and* per-endpoint bytes. Detect elevation or Performance Log Users membership at startup
  and register it with a higher `Priority`. Biggest fidelity jump available, and it slots in behind
  the existing interface.
- **Per-connection bytes** via `SetPerTcpConnectionEStats` / `GetPerTcpConnectionEStats`. Admin-only,
  so pair it with the ETW path.
- **Budget alerts** when an application crosses a monthly threshold. Genuinely useful on a capped or
  metered link.
- **GeoIP enrichment** of the connections view from a local MaxMind database — no network calls.
- **System tray** with live up/down text and a top-five flyout.
- **Metered-link awareness** via `ConnectionProfile.GetConnectionCost()`, to separate cellular usage.

## Notes on the dependencies

| Package | Why |
|---|---|
| `CommunityToolkit.Mvvm` | Source-generated observable properties and commands. No reflection. |
| `Microsoft.Extensions.Hosting` | DI, options binding, and `BackgroundService` lifecycle for the sampler. |
| `Microsoft.EntityFrameworkCore.Sqlite` | Querying and model. The hot write path and the rollup are raw SQL. |
| `LiveChartsCore.SkiaSharpView.WPF` | Skia-rendered charts that survive thousands of points and update live. |
| `Serilog.*` | Rolling file logs in the data folder. Diagnosing a blocked NDU from a user's log is the point. |

WinRT projections need no package — the `net10.0-windows10.0.19041.0` target framework brings them.

Package versions in `Directory.Packages.props` are the expected .NET 10 GA wave. Run
`dotnet list package --outdated` after the first restore; LiveCharts2 in particular moves fast.

Do not enable trimming. WPF and CsWinRT both rely on reflection the trimmer cannot see.
