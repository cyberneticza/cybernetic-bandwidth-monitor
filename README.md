# Cybernetic Bandwidth Monitor

A per-user network usage tracker for Windows 10/11. Shows which applications are using your
bandwidth, keeps the history locally, and charts it over periods from a day to a year. It also
watches the connection itself: what you are connected to, whether it is healthy, when it dropped or
switched, and how fast it really is.

Runs as a standard user. No driver, no UAC prompt, nothing written outside your own profile. The one
exception is opt-in: a Windows service, set up from Settings, for recording from Windows startup (see
[Running in the background](#running-in-the-background)).

Current version: see `VersionPrefix` in `Directory.Build.props`. What changed between versions is in
[CHANGELOG.md](CHANGELOG.md).

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

Installs to `%LocalAppData%\Programs\CyberneticBandwidth` with a Start Menu shortcut and a per-user uninstall
entry. `build\uninstall-peruser.ps1` reverses all of it; add `-KeepData` to leave your history.

**Upgrading** is the same two scripts. Exit Cybernetic Bandwidth Monitor from its tray icon first: a running copy locks
the exe and the install fails to overwrite it. The database upgrades itself on first start and keeps
its history.

**Coming from NetTrace**, this app's earlier name: install as above. The script removes the NetTrace
install (and refuses while NetTrace's Windows service is installed; remove that from NetTrace's Settings
first). On its first start the app moves `%LocalAppData%\NetTrace` to its own folder, renaming the
database and logs, and replaces NetTrace's sign-in entry with its own (`LegacyNetTrace`,
`SignInStartup`). A folder it cannot move, because NetTrace is still running or the new folder
already has data, is left alone and the reason is logged.

Collected data lives in `%LocalAppData%\CyberneticBandwidth`: `cyberneticbandwidth.db`, `preferences.json` and seven days
of rolling logs. While the optional service is installed, the database is in `%ProgramData%\CyberneticBandwidth`
instead.

**Other devices.** `pwsh -File build\make-transfer.ps1` publishes, then builds `artifacts\transfer`
with one folder per way of putting Cybernetic Bandwidth Monitor on another device, each holding every file it needs and
an install guide PDF (install, upgrade, uninstall, the exact commands, and installing PowerShell 7):

| Folder | For |
|---|---|
| `Option1-Portable` | Copy `CyberneticBandwidth.exe` anywhere and run it. No scripts; works from Windows PowerShell 5.1. |
| `Option2-Install` | The per-user install above: `app\` plus the install and uninstall scripts. Needs `pwsh`. |

The guides are HTML templates in `build\transfer` (shared styles in `guide.css`, the PowerShell 7
section in `pwsh-section.html`), printed to PDF by headless Microsoft Edge. Pass `-SkipPublish` to
reuse the current `artifacts\portable` build.

`build\make-icon.ps1` regenerates `src\CyberneticBandwidth.App\Assets\CyberneticBandwidth.ico` from code; pass
`-PreviewPath` to also write each size as a PNG.

## How it measures, and what that costs you

Monitoring without administrator rights rules out almost everything a network monitor normally
does. Packet capture needs a driver. A WFP callout needs the kernel. ETW's kernel network provider
needs `SeSystemProfilePrivilege`. So Cybernetic Bandwidth Monitor fuses sources that a standard user *can* read:

| Source | Gives | Refresh |
|---|---|---|
| Windows Data Usage (NDU), via `ConnectionProfile.GetAttributedNetworkUsageAsync` | Bytes sent and received, attributed per application | ~1 minute |
| `GetExtendedTcpTable` / `GetExtendedUdpTable` | PID, protocol, local and remote endpoints, TCP state | 5 seconds |
| NIC counters | Machine-wide throughput for the live strip | 1 second |
| Connection profiles, adapters, Network List Manager, Native Wifi | Network name, type, link speed, signal, addresses, metered state | On change, and every 15 seconds |
| Ping to the router, HTTPS to Cloudflare | Connection quality: latency, jitter, failed requests | 30 seconds |
| speed.cloudflare.com endpoints | Download, upload, latency and jitter to the nearest data centre | When the user runs a test |
| Vultr public speed-test hosts in ten cities | Latency and download to each part of the world | When the user runs a regional test |
| LibreSpeed public servers in five of those cities | Upload to that part of the world | When the user runs a regional test |

The last four are the only network traffic Cybernetic Bandwidth Monitor creates; see
[The connection itself](#the-connection-itself).

Two consequences worth internalising before you extend this:

**Resolution is about a minute.** Anything finer would be invented. The UI never claims otherwise.

**Byte counts and sockets use different name spaces.** NDU attributes to applications; the
connection table is keyed by PID. They do not line up reliably, so the live view presents them as
two separate tables rather than joining them into one convincing-looking lie.

**NDU names nothing for an unpackaged caller.** `AttributionName` is only filled in for apps running
inside a package, so Cybernetic Bandwidth Monitor always gets it empty. `AttributionId` still identifies the app — an NT
device path for desktop programs, a package family name for Store apps, `System\...` for the
kernel — and `AppNameResolver` turns that into the name from the exe's version resource or the
package manifest. Stored names are re-derived on every start. Traffic that arrives with an empty id
is genuinely unattributed; on machines with a VPN or security proxy client, expect that client to
own most of the bytes, because it is the process that puts them on the wire.

## Running in the background

Cybernetic Bandwidth Monitor lives in the notification area. Closing the window hides it and collection carries on;
**Exit** on the tray menu stops it. Launched with `--minimised` (which is what start at sign-in
does), it starts in the tray without opening the window. Starting it again while it runs brings
the existing window forward. Windows 11 puts new tray icons in the overflow flyout; drag it onto
the taskbar, or turn it on under Settings > Personalization > Taskbar > Other system tray icons.

No installer sets up either of the two ways below. Both are opt-in, from **Settings › Running in the
background**.

### Start at sign-in

A Task Scheduler task for the signed-in user, `Cybernetic Bandwidth Monitor at sign-in (<user>)` (`SignInStartup`): a
logon trigger with a 5-second delay, running `CyberneticBandwidth.exe --minimised` with the user's own token, no
time limit and no battery conditions. A task rather than the `Run` registry key, because Windows holds
`Run`-key apps back after sign-in and Task Manager's startup list can switch them off. Creating a task
for your own account needs no administrator rights. If Task Scheduler refuses, the `Run` key is the
fallback.

Every start checks it (`RefreshIfEnabled`): a task whose program has gone (the folder moved) is pointed
at this copy, and a `Run` entry for this copy (from `install-peruser.ps1 -Autostart` or an older
version) becomes a task. An entry for another copy that still exists is left alone.

### The Windows service

For recording from Windows startup, whether or not anyone is signed in, and while no window is open.
One program runs three ways (`Program.Main`, `CollectorMode`):

| Started as | Mode | Does |
|---|---|---|
| `CyberneticBandwidth.exe --service` | `Service` | The collector with no window: sources, sampler, watcher, health monitor, plus `CollectorPipeServer` |
| `CyberneticBandwidth.exe` while the service is installed | `Viewer` | The window without a collector: live figures come over the collector link, history from the shared database |
| `CyberneticBandwidth.exe` otherwise | `InProcess` | The usual app, collecting in the window's own process |

**Installing.** Settings starts `CyberneticBandwidth.exe --install-service --result <file>` behind a UAC prompt.
That elevated copy (`CollectorServiceSetup`):
1. copies the program to `%ProgramFiles%\CyberneticBandwidth`;
2. prepares `%ProgramData%\CyberneticBandwidth`;
3. registers the service `CyberneticBandwidth` ("Cybernetic Bandwidth Monitor collector") as LocalSystem with `start= delayed-auto`,
   restart on failure, and a descriptor that lets interactive users start and stop it.

The window then copies its history into the shared folder (SQLite `VACUUM INTO`, a consistent
snapshot of a database in use), starts the service and restarts itself as a viewer. **Update** re-runs
the install over the top when the window's version differs from the service's. **Remove** stops and
deletes the service, deletes the Program Files copy, copies the shared history back to
`%LocalAppData%\CyberneticBandwidth` and restarts the window, which then collects itself again.

**The collector link.** A named pipe, `CyberneticBandwidth.Collector.v1`, carrying one JSON message per line
(`CollectorLink`). On connect the service sends `hello` (version, data folder, the health-check
switch), then the latest status, network and health. After that it relays every `LiveActivityBroker`
event: usage, connections, connection events, throughput, status, network, health and network events.
Each client has a bounded queue that drops the oldest messages, so a stalled window cannot hold the
service up. The window republishes what arrives into its own broker, so pages cannot tell a viewer
from the usual app. The only message the other way is `setHealthChecks`: the health-check switch
belongs to the service, which keeps it in `service.json`. The window retries every 3 seconds, and
shows a banner while the service is not answering.

**Folders and security.**
- **The program** runs from Program Files. A service must never run from a folder its user can write
  to, or anything running as that user could swap the exe and run as SYSTEM.
- **`%ProgramData%\CyberneticBandwidth`** holds the database and `service.json`. Users may change the files in it
  but not the folder itself, which the install takes ownership of and refuses when it is a link. A
  user who could rename the folder and put a junction in its place could send the service's writes to
  any file on the disk.
- **`logs`** in that folder is the service's alone, read-only to users. A viewer window logs to
  `%LocalAppData%\CyberneticBandwidth\logs\cyberneticbandwidth-window-*.log`, and keeps `preferences.json` there too.
- **The pipe** refuses network logons and is open to authenticated local users. It carries the same
  figures any user of the PC can read in the shared database.

**What is not known yet.** The service has only been exercised from a console (below) under a
standard account. Whether NDU still attributes bytes per application when called from session 0 as
LocalSystem has not been checked; the service logs which source it picked
(`%ProgramData%\CyberneticBandwidth\logs\cyberneticbandwidth-service-*.log`). Try it on a machine where you are an
administrator before relying on it.

**Testing without installing.** Run the collector and a viewer side by side against a scratch folder;
neither needs administrator rights. Exit the usual Cybernetic Bandwidth Monitor first (a viewer is still single-instance):

```powershell
$env:Storage__DataDirectory = "$env:TEMP\cyberneticbandwidth-service-test"
Start-Process .\CyberneticBandwidth.exe --service     # the collector, no window
Start-Process .\CyberneticBandwidth.exe --viewer      # a window that shows it
```

Stop the `--service` process to see the banner, and start it again to see the window reconnect.

## Screens

- **Live** — download and upload rates, the last five minutes of throughput, today's total since
  local midnight (top right), traffic by application for this session, and open sockets.
- **Trends** — one column per day for the chosen period (7 days to a year). Hold Ctrl and scroll to zoom, drag to
  pan, double-click to reset, click a day to open it. The selected day is charted underneath,
  stacked by application, with a table of that day's applications; tick some to chart only
  those. Detail is **Auto** by default: 5-minute bins where minute data covers the whole day
  (the last week, while Cybernetic Bandwidth Monitor was running), hourly otherwise. 1, 5 and 15 minutes and hourly
  can be chosen directly, and the chart says when the finer data only covers part of the day.
  Hovering the day chart lists every application in that time slot, and any network events in it.
- **History** — a report for any range of days: quick ranges (last 7, 30 or 90 days, this or last
  month, this year, last 12 months, everything recorded) or a custom range picked on a calendar.
  Summary cards (total, daily average with the typical day and 95th percentile, busiest day,
  busiest time, top application) with change against the previous period of the same length; a
  plain-language list of what stands out; usage per day, week or month with its average, 7-day trend,
  unusually heavy days and the previous period overlaid; a calendar heatmap of busy days; average
  use by weekday; a weekday-by-hour heatmap of busy hours; the top applications over time and in a
  table; and a day-by-day log. Both tables export to CSV, and any day opens in Trends.
- **Network** — what you are connected to (network, Wi-Fi signal, link speed, addresses, metered,
  any VPN or security proxy in the path), connection quality now and over time, and a log of drops,
  network switches, VPN changes and slow spells.
- **Speed test**, in two tabs. **Local test**: download, upload, response time and jitter against
  Cloudflare's nearest data centre. **Around the world**: response time and download to fixed
  servers in ten cities across the continents. Each tab has its own plain reading of what the
  results are good for (calls, 4K, gaming, backups; per continent for the regional test), its own
  chart and table of past results, and a checkbox to compare the latest test with the previous one.
- **Connections** — the socket open/close log, with an explanation of how it is gathered and how to
  read it, and a column naming each port's usual service.
- **Settings** — appearance, quality checks, heavy-use alerts and data budgets, storage, sampling
  and start-with-Windows.
- **About** — what the app does, the author, and the version and build details.

**History, how it is worked out.** `UsageHistoryReport` (Core) takes the range's days, per
application and, within the hour retention, per local hour. The measures follow what traffic
dashboards and usage meters commonly report: period totals and change against the period before
(Google Analytics-style comparison), a typical (median) and 95th-percentile day, top applications
("top talkers" in NetFlow tools), day-of-week and hour-of-day patterns, a calendar heatmap, and
usage against the monthly budget with a projection to the end of the cycle (as ISP usage meters do).
Averages and patterns cover the *recorded* span only, from the first day with traffic in the
range, so installing Cybernetic Bandwidth Monitor halfway through a month does not halve its average. A day is
"unusually heavy" when, with at least seven recorded days, it is more than two standard deviations
above the average and at least 1.5 times it. A previous period with nothing recorded is not compared
with, since every application would otherwise read as new. The calendar shades days in quarters of
the days with traffic, so one enormous day does not wash out the rest; the busy-hours heatmap uses
the days that still have hour detail and says so when that is only part of the range.

Days on Trends and History and "today" on Live are local calendar days. Buckets are stored in UTC, so both are
assembled from hour buckets; only history older than the hour retention (120 days) falls back to
the stored UTC day buckets, which can be a few hours off at the day's edges.

## The connection itself

**Quality checks.** Every 30 seconds `ConnectionHealthMonitor` pings the router three times and
makes three small HTTPS requests to a Cloudflare edge. The two together separate "my Wi-Fi or local
network" (slow or silent router) from "the internet" (quick router, slow or failing internet). HTTPS
rather than ping for the internet leg because ICMP is often blocked on corporate and public
networks, and because it measures the path web traffic really takes. Each check is a few kilobytes,
about 10 MB a day; on metered connections only one check in ten runs. Thresholds live in the
`Health` section of `appsettings.json`, and a state change has to hold for two checks before it is
recorded as an event.

**Events.** `NetworkWatcherService` re-reads the network on Windows' change notifications and every
15 seconds, and records the differences: disconnected, reconnected (with how long it was down),
switched network, captive portal, VPN or proxy on or off. Events are marked on the Trends day chart
and listed on the Network page, and kept a year.

**Speed test.** `SpeedTestService` uses the public endpoints behind speed.cloudflare.com. Latency is
the median of ten empty responses on a warm connection. Download and upload each run for a fixed
time (10 s and 8 s), growing the request size while requests finish within a second, and leave out
the first 1.5 s of TCP ramp-up. A test uses roughly 50 to 300 MB; on a metered connection the page
asks first. Results are stored with the network they were taken on.

Cloudflare is anycast, so that test always reaches the nearest data centre and cannot be pointed at
another continent. **Around the world** uses fixed-location servers instead: Vultr's public
speed-test hosts (`<region>-ping.vultr.com`, a 100 MB file that honours range requests) in ten cities
listed in `SpeedTestService.Regions`. Each region gets two warm-up requests (through a proxy the
second still pays for the proxy's onward connection), five timed 1-byte range requests for latency,
and six seconds of ranged downloads, with a 25-second cap per region so one slow server cannot stall
the run. Regions run one at a time, never in parallel with each other or with the Cloudflare test.
A region whose download never starts within its cap is stored as not measured rather than 0 Mbps.

Vultr's servers accept no uploads, so upload goes to a second server: one from LibreSpeed's public
list (`librespeed.org/backend-servers/servers.php`), which takes POSTed bytes at `empty.php`. That
list has servers in London, Frankfurt, New York (used for New Jersey, about 15 km away; the page says
so), Los Angeles and Tokyo, and none in Johannesburg, Mumbai, Singapore, São Paulo or Sydney, which
therefore measure no upload. Upload runs after the download for six seconds with its own 25-second
cap, the same way as the Cloudflare upload: random bytes in requests growing from 1 MB, each timed
from send to the server's response. An upload that fails keeps the region's download and records the
reason. Where the list changes, `SpeedTestService.Regions` is the one place to update. A run of all
ten takes about two minutes.

Comparing with the previous test (`ChangeInfo` in `SpeedTestViewModel`) treats differences under 3%
as "≈ same", since two back-to-back tests never match exactly. The Local tab compares the test on
screen with the one before it; Around the world compares each region with its own previous result.
The continent verdicts use each continent's quickest-answering server.

**VPNs and security proxies.** Netskope, Zscaler and similar agents steer traffic without an adapter
of their own, so Cybernetic Bandwidth Monitor detects them by their running service; VPNs are detected by an adapter
that is actually up, which avoids reporting a VPN whose driver is merely installed. When one is in
the path, quality, speed and even the per-app byte counts describe the tunnel, and the UI says so.

**Alerts and budgets.** `UsageAlertService` watches the per-minute usage: a tray notification when
one application sustains more than the chosen MB a minute for three minutes (once an hour per app),
and at 80% and 100% of an optional daily or billing-month budget (once per level per period, even
across restarts).

**Privacy.** Apart from the quality checks (which can be turned off) and speed tests the user starts
(to Cloudflare, and to Vultr and LibreSpeed servers for the regional test), Cybernetic Bandwidth Monitor makes no network requests, and nothing
it records leaves the PC.

## Light and dark

**System** (the default) follows the Windows app theme and switches when Windows does; **Light**
and **Dark** pin one. Change it under Settings > Appearance. The choice is saved to
`%LocalAppData%\CyberneticBandwidth\preferences.json`.

Colours live only in `Themes/Dark.xaml` and `Themes/Light.xaml`, which define the same keys; add a
key to both. Views must use `DynamicResource` for anything colour-related or it will not follow a
theme switch. Charts are drawn by Skia outside WPF's resources, so their view models rebuild paints
from `ThemeService.Chart` on `ThemeChanged`.

## Look and feel, and window sizes

The design system is three files. `Themes/Tokens.xaml` holds type sizes (hero figures, page titles,
headings, body), corner radii (cards softer than controls) and page padding. The palettes add a
soft tint of each meaning colour (`DownloadSoftBrush`, `UploadSoftBrush`, `AlertSoftBrush`) for icon
badges, and `BrandBrush`, the icon's two directions as a gradient. `Themes/Controls.xaml` has the
shared styles: `Panel` cards, `IconBadge`, `HeroNumber`, `EyebrowText` and `PageTitleText`; buttons
that wash on hover and press in; a text box whose `Tag` is its placeholder; a rounded accent focus
ring for keyboard users; and the side rail's pill-shaped items.

The shell gives every page a title and a one-line description (from its `NavigationItem`), fades and
lifts a page into place when it opens, and shrinks the rail to icons with tooltips when the window
is narrower than 1100 DIP. The window goes down to 760 × 540 DIP, which fits a 1280 × 720 screen at
150% scaling.

Pages adapt through `Controls/Layout.cs`, with one flag per page:

| Piece | What it does |
|---|---|
| `Layout.CompactBelow` | On a page root: below that width (720 DIP on every page) `Layout.IsCompact` turns on for the whole page |
| `AdaptiveColumns` | Side by side by `Weight` or `FixedWidth` while wide; stacked full width while compact, each child `StackedHeight` tall |
| `AdaptiveGrid` | Equal tiles, as many across as fit at `MinItemWidth`, balanced so four go 2 × 2 rather than 3 + 1 |
| `Layout.FillViewport` | On a page grid inside a ScrollViewer: fills the window while there is room, scrolls on a short window, and turns star rows into Auto rows while compact |
| `Layout.PassWheelAtEnd` | On a table or inner scroller: the wheel moves the page once the inner one can go no further (on by default for every DataGrid) |
| `ChartTooltip.UsePopup` | On every `CartesianChart` (set by the implicit chart style in `Controls.xaml`): hover details open in a popup beside the pointer instead of LiveCharts' tooltip drawn inside the chart, which is cut off at its edges. The title is the X axis's label for the hovered point; rows are series swatch, `Name` and `YToolTipLabelFormatter` value, so formatters should return just the value |
| `ChartZoom.RequireCtrl` | On a zoomable chart: the wheel zooms only with Ctrl held; otherwise it scrolls the page and a "Hold Ctrl and scroll to zoom" pill appears over the chart. Say how to zoom in the chart's reset-zoom button tooltip rather than in a line above the chart |
| `ChartScale` | For every chart axis: `LabelSize` for its `TextSize`, and `Bytes(axis, MaxOf(series))` for a byte scale in round steps (1, 2 or 5 of a binary unit). Axes built without it read 953.7 MB, 1.9 GB |

`Layout.FillViewport` gives its grid a real height from the first layout pass. Without that, a
ScrollViewer offers unlimited height before the page has loaded, and a DataGrid measured that way
switches off virtualisation and builds every row. That is what froze the app for about 37 seconds
whenever Connections, with its 2,000 to 5,000 rows, was opened.

A new page wraps its content in a ScrollViewer, sets `CompactBelow`, and uses these panels for
anything that sits side by side, so it scales without per-page code.

## Configuration

Two files, for two audiences.

**`appsettings.json`**, next to the exe, is how the collector behaves. An update overwrites it.

| Section | Controls |
|---|---|
| `Sampler` | Polling intervals, how far each usage pass re-reads, first-run backfill, idle back-off |
| `Retention` | How long minute, hour and day buckets, connection events, health samples and network events are kept |
| `Health` | Quality-check interval, the HTTPS probe URL, probes per check, the Slow thresholds, and how many checks must agree before an event |

**`%LocalAppData%\CyberneticBandwidth\preferences.json`** holds what the user chose in the app: theme, whether
quality checks run, heavy-use alert threshold, budgets and their reset day, which budget warnings
were already shown, and whether the Connections explanation is visible. Settings writes it; an
unreadable file is ignored rather than stopping the app.

## Versioning and releases

The version is `VersionPrefix` in `Directory.Build.props`, as major.minor.patch: patch for fixes,
minor for features, major for changes that break the database or the install layout. It flows into
the exe's file properties, the About page, the side rail and the Settings > Apps entry.

To release:

1. Move the entries under **Unreleased** in [CHANGELOG.md](CHANGELOG.md) into a new version heading
   with today's date.
2. Set `VersionPrefix` to that version.
3. `pwsh -File build\make-transfer.ps1` (publishes too), then install or distribute `artifacts\transfer`.

## Layout

```
src/CyberneticBandwidth.Core    Sources, interop, sampler, health monitor, speed test, domain models. No UI, no database.
src/CyberneticBandwidth.Data    SQLite schema, repositories, rollup and retention.
src/CyberneticBandwidth.App     WPF shell, pages and view models, theme, tray icon, alerts, preferences.
build/               Publish, install, uninstall, transfer packages and guides, probe, icon generation.
```

`INetworkUsageSource` is the seam that matters. The sampler picks the highest-priority source that
reports itself available and knows nothing else about it, so adding an elevated ETW collector later
is a new class and one DI registration — nothing above it changes.

Background work runs as hosted services in Core, each on its own cadence: `NetworkSamplerService`
(usage, sockets, throughput), `RollupHostedService`, `NetworkWatcherService` and
`ConnectionHealthMonitor`. They publish through `LiveActivityBroker`, which marshals to the UI
thread and keeps the latest network snapshot and health sample for pages opened later. Nothing in
Core references WPF; switches the user controls reach Core through `MonitoringSettings`.
`HostingExtensions.AddCyberneticBandwidth` registers these per `CollectorMode`: all of them in the usual app and
the service, none in a viewer, which gets the broker's events over the collector link instead.

Pages resolve by view-model type through `DataTemplate`s in `App.xaml`, and the view models are
singletons. Pages that read history (Trends, History, Network, Speed test) reload when navigated to.

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

Reads that span granularities (`GetTimelineAsync`) fill each bin from every granularity no coarser
than the bin and keep whichever recorded the most. The newest hour exists only as minutes until the
next rollup, and history from before the first run exists only as backfilled hours, so summing
across granularities would double count and reading one would leave gaps.

The connection itself has its own tables, plain appends indexed by time:

| Table | One row per | Kept for |
|---|---|---|
| `ConnectionEvents` | Socket opened or closed | 14 days |
| `HealthSamples` | Quality check (every 30 s) | 30 days |
| `NetworkEvents` | Disconnect, reconnect, switch, VPN change, outage, slow spell | 1 year |
| `SpeedTests` | Nearest-server speed test | Indefinitely |
| `RegionalSpeedTests` | Region tested in an "Around the world" run (the page shows the latest per region, and the one before when comparing) | Indefinitely |

The schema is explicit DDL in `SchemaInitializer`, all `CREATE … IF NOT EXISTS`, with a version
number in `SchemaInfo`. Adding a table is additive and needs no migration; bump `SchemaVersion` so
the log records the upgrade.

## Keeping it cheap

Three independent cadences, pooled buffers in the interop layer, one transaction per tick rather
than per row, WAL journaling, in-place merges into the app table instead of rebuilding it, and a
4x interval stretch once `GetLastInputInfo` says you have been away five minutes.

Only socket *transitions* are persisted. Writing the whole table every five seconds would add
roughly half a million rows a day and tell you nothing. Each poll resolves every owning process once,
not once per socket.

The network watcher re-reads on Windows' change notifications, settling a burst of them into one
read, plus a slow 15-second tick for signal strength. Quality checks reuse one warm HTTPS connection
so they measure round trips rather than handshakes, and thin to one in ten on metered connections.

## Troubleshooting

The log in `%LocalAppData%\CyberneticBandwidth\logs` is the first stop; Settings and About both open the folder.
While the service is installed, its own log is in `%ProgramData%\CyberneticBandwidth\logs`.

| Symptom | Likely cause |
|---|---|
| Most traffic belongs to one security or VPN client | It carries everyone's traffic. Byte counts, quality and speed describe the tunnel; see the Network page |
| Speed test download far below upload, or very different between runs | A security proxy buffering downloads before passing them on. Compare a test with the proxy off, if you are allowed to |
| Wi-Fi signal shows as a coarse value or "unknown" | Windows 11 24H2 and later withhold detailed Wi-Fi data unless location access for desktop apps is on; Cybernetic Bandwidth Monitor falls back to signal bars |
| Router latency shows "no reply" | The router ignores ping. Quality still works from the internet check; only the local/internet split is lost |
| No tray icon | Windows 11 puts new icons in the overflow flyout; see [Running in the background](#running-in-the-background) |
| Install fails to copy `CyberneticBandwidth.exe` | Cybernetic Bandwidth Monitor is still running. Exit it from the tray, then install again |
| Banner: "The Cybernetic Bandwidth Monitor service is not answering" | The service is stopped or starting. Settings › Running in the background shows its state and can start it |
| Settings offers **Update service** | The window and the service are different versions, usually after upgrading the window. Update copies this version over |
| `uninstall-peruser.ps1` stops at once | The service is installed. Remove it in Settings first, which also brings the history back to your profile |
| Trends detail is hourly for a recent day | Minute data does not cover the whole day, usually because Cybernetic Bandwidth Monitor was not running for part of it |

## Where to take it next

- **Elevated ETW collector.** `Microsoft-Windows-Kernel-Network` gives packet-accurate, sub-second,
  per-PID *and* per-endpoint bytes. Detect elevation or Performance Log Users membership at startup
  and register it with a higher `Priority`. Biggest fidelity jump available, and it slots in behind
  the existing interface.
- **Per-connection bytes** via `SetPerTcpConnectionEStats` / `GetPerTcpConnectionEStats`. Admin-only,
  so pair it with the ETW path.
- **Per-application budgets**, building on the daily and monthly ones: a cap for one app, such as a
  cloud backup on a hotspot.
- **GeoIP enrichment** of the connections view from a local MaxMind database — no network calls.
- **Top-five tray flyout.** The tray icon exists and its tooltip carries live up/down rates; a flyout
  listing the heaviest applications would make opening the window unnecessary most of the time.
- **Usage per network.** NDU already reports per connection profile; storing the profile rather than
  only the medium would split home Wi-Fi from office Ethernet from a phone hotspot on Trends.

## Notes on the dependencies

| Package | Why |
|---|---|
| `CommunityToolkit.Mvvm` | Source-generated observable properties and commands. No reflection. |
| `Microsoft.Extensions.Hosting` | DI, options binding, and `BackgroundService` lifecycle for the sampler. |
| `Microsoft.EntityFrameworkCore.Sqlite` | Querying and model. The hot write path and the rollup are raw SQL. |
| `LiveChartsCore.SkiaSharpView.WPF` | Skia-rendered charts that survive thousands of points and update live. |
| `Serilog.*` | Rolling file logs in the data folder. Diagnosing a blocked NDU from a user's log is the point. |

WinRT projections need no package — the `net10.0-windows10.0.19041.0` target framework brings them.
The tray icon is WinForms' `NotifyIcon` (`UseWindowsForms` in the App project), part of the Windows
Desktop runtime rather than a package; it adds about 11 MB to the self-contained publish. HTTP for
the quality checks and speed test is plain `HttpClient`.

Package versions in `Directory.Packages.props` are the expected .NET 10 GA wave. Run
`dotnet list package --outdated` after the first restore; LiveCharts2 in particular moves fast.

Do not enable trimming. WPF and CsWinRT both rely on reflection the trimmer cannot see.
