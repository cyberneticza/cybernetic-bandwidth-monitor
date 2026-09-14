# Changelog

All notable changes to Cybernetic Bandwidth Monitor. The format follows [Keep a Changelog](https://keepachangelog.com/en/1.1.0/),
and versions follow [Semantic Versioning](https://semver.org/): patch for fixes, minor for features,
major for changes that break the database or the install layout. The version itself lives in
`VersionPrefix` in `Directory.Build.props`.

## [Unreleased]

### Added

- **History page.** A report for any range of days, from quick ranges (last 7, 30 or 90 days, this
  or last month, this year, last 12 months, everything recorded) or a custom range picked on a
  calendar that marks the days with usage. It shows:
  - summary cards: total, daily average with the typical day and 95th percentile, busiest day,
    busiest time of day and top application, each compared with the previous period of the same
    length;
  - "What stands out": plain-language findings such as the change against the previous period, the
    busiest day and what drove it, unusually heavy days, weekdays against weekends, the busiest
    hours, a high upload share, applications that grew or are new, network problems, and the
    monthly budget with a projection to the end of the cycle;
  - usage per day, week or month with its average line, 7-day trend, unusually heavy days and the
    previous period overlaid;
  - a calendar heatmap of busy days, average use by weekday, and a weekday-by-hour heatmap of busy
    hours;
  - the top applications over time and a table of every application with its share, days used,
    busiest day and change;
  - a day-by-day log with each day's top application, busiest hour, network problems and notes.

  Both tables export to CSV, and any day opens in Trends.
- **Speed test around the world.** The Speed test page has two tabs. **Local test** is the
  nearest-Cloudflare test; **Around the world** measures response time and download speed to test
  servers in fixed cities: Johannesburg, London, Frankfurt, Mumbai, Singapore, New Jersey, São Paulo,
  Los Angeles, Tokyo and Sydney. Test every region in turn or just one. Cloudflare's own test cannot
  do this: it always answers from the nearest data centre. The regional servers are Vultr's public
  speed-test hosts (see upload around the world, below).
- **Upload around the world.** The Around the world test now measures upload too, in London,
  Frankfurt, New Jersey (to a server in New York), Los Angeles and Tokyo, using LibreSpeed's public
  test servers, since Vultr's take no uploads. Johannesburg, Mumbai, Singapore, São Paulo and Sydney
  have no such server, so their upload shows "—" with the reason on hover. Upload appears in the
  region list, the continent verdicts (a weak upload marks video calls down), the By region chart as
  its own bars, the past tests table and the comparison with the previous test. Response-time dots
  on that chart are now drawn in the text colour. A region takes about 15 seconds instead of 8. The
  database schema moves to version 4 with an `UploadMbps` column; the upgrade is automatic.
- **Each speed test tab has its own verdicts, chart and table.** Local test: "What this connection is
  good for", speed over time and past tests side by side. Around the world: "What these connections
  are good for" per continent (judged from its quickest server), download and response time by
  region, and every past regional result.
- **Compare with the previous test**, a checkbox on each tab. Local test shows how download, upload,
  response time and jitter moved since the test before (better in teal, worse in red, "≈ same"
  within 3%), highlights both tests in the table and notes any verdict that flipped. Around the world
  compares each region with its own previous result in the list, the verdicts and the chart (faded
  bars and hollow dots).
- **Hover detail on the Trends day chart**: the time slot's download and upload totals, every
  application in it with its colour and amount (the top twelve, then a count of the rest), and any
  network events in that slot.
- **Transfer packages for other devices.** `build\make-transfer.ps1` builds `artifacts\transfer`
  with `Option1-Portable` (copy the exe and run it) and `Option2-Install` (per-user install with a
  Start Menu shortcut and an entry in Settings › Apps). Each folder holds every file that option
  needs and a PDF install guide with install, upgrade and uninstall steps, the exact commands, and
  how to install PowerShell 7 when it is missing.
- **Run as a Windows service (optional).** Settings › Running in the background › **Install service**
  records from Windows startup, whether or not anyone is signed in, and while Cybernetic Bandwidth Monitor's window is
  closed. It asks for administrator approval once, copies the program to `%ProgramFiles%\CyberneticBandwidth`,
  and moves the history to `%ProgramData%\CyberneticBandwidth`, where every user of the PC can see it. The window
  then shows the service's data and figures. Settings shows whether the service is running and can
  start it without another prompt, update it to the window's version, or remove it, which brings the
  history back to your own profile. While the service is not answering, a banner says so and the
  window reconnects on its own. Nothing installs the service unless you ask for it.

### Changed

- **NetTrace is now Cybernetic Bandwidth Monitor.** The window, tray, Start Menu and Settings › Apps
  show the new name. The program is `CyberneticBandwidth.exe`, installed to
  `%LocalAppData%\Programs\CyberneticBandwidth`, and keeps its data in `%LocalAppData%\CyberneticBandwidth`
  (`cyberneticbandwidth.db`). On its first start it moves the history, preferences and logs over from
  `%LocalAppData%\NetTrace` and takes over NetTrace's start-at-sign-in entry. `install-peruser.ps1`
  removes a per-user NetTrace install and stops if NetTrace's Windows service is still installed: remove
  that from NetTrace's Settings first. The logo is unchanged; the side rail shows the name on two
  lines, "Cybernetic" over "Bandwidth Monitor".
- **The Live throughput chart runs steadily.** It always shows the last five minutes, with new
  seconds arriving on the right from the moment the app starts, and redraws without animation, so
  it no longer appears to zoom in and out as traffic comes and goes. Its scale changes in doublings
  (16 KB/s, 32 KB/s, …) instead of following every burst.
- **Start at sign-in uses a scheduled task** (Settings › Running in the background) instead of the
  `Run` registry key, so Cybernetic Bandwidth Monitor starts promptly after sign-in and Task Manager's startup list does
  not switch it off. It still needs no administrator rights. An existing `Run` entry for the same copy
  moves over to the task on the next start, and a task left pointing at a moved program is pointed at
  the copy you start. `uninstall-peruser.ps1` removes the task too, and stops without changing
  anything while the service is installed.
- On the very first start, while Cybernetic Bandwidth Monitor reads up to 14 days of history from Windows (a few minutes
  on a busy machine), a banner explains why the live figures have not started yet.

- **A refreshed design.** Every page has a title and a one-line description, and opens with a short
  fade. The side rail has the app icon, pill-shaped page items with a lit icon for the current
  page, and a keyboard focus ring. Cards are softer, headline figures are larger, and tinted icon
  badges mark what each card is about. Live leads with download, upload, today and open sockets as
  cards, with a pulsing "live" marker; Network, Settings and About get icon badges and a bolder
  quality reading. Buttons respond to hover and press, and text boxes show a placeholder.
- **Scales to any window size.** The window now goes down to 760 × 540 (from 1020 × 640), enough
  for a small laptop at 150% scaling. Below 1100 wide the side rail shows icons only. Below about
  720 of page width, side-by-side panels stack into one column and summary cards wrap into balanced
  rows. Pages that fill the window scroll instead of squashing on a short one, toolbars wrap
  instead of overlapping, and tables keep readable columns and scroll sideways when they must.
- Scrolling with the mouse wheel over a table or the History calendar moves the page once that
  table or calendar can go no further.
- **Charts zoom with Ctrl + scroll.** On Trends (both charts) and Network's quality chart, the wheel
  alone now scrolls the page; hold Ctrl and scroll to zoom. Scrolling over a chart without Ctrl shows
  a brief "Hold Ctrl and scroll to zoom" hint, and each chart's header says so. Drag to pan and
  double-click to reset work as before.
- Settings and About move to the bottom of the side rail.
- The light/dark toggle is gone from the side rail. The theme is chosen only under Settings ›
  Appearance (System, Light or Dark).
- The Speed test page scrolls, so every region and the history get their full height.
- Network event markers on the Trends day chart that sit close together share one label ("Slow +1")
  instead of printing over each other.
- The database schema moves to version 3 with a `RegionalSpeedTests` table. The upgrade is automatic.

### Fixed

- Live's "Traffic by application" table showed a stale "Last minute" rate: an application that
  stopped using the network kept its old rate indefinitely, and one that was still active could show
  the rate of an older minute. It is now each application's traffic in the last finished minute,
  refreshed for every row each minute, and falls to 0 B/s for applications that went quiet.
- Chart hover details were cut off at the chart's edges, worst on History's stacked "Top
  applications over time" with seven rows. Every chart's hover details now open in a panel beside
  the pointer that can extend past the chart and flips to the other side near the window's edges.
  Each row shows the series' colour, name and value, under the hovered date or label.
- Opening Connections froze the whole app for 15 to 40 seconds. The scrolling page layout let its
  log table build all of its thousands of rows at once instead of only the visible ones; opening
  it now takes about a tenth of a second.
- The Live throughput chart could put zero at the top, so a quiet download with upload bursts read
  upside down. The scale is now the same above and below zero, labelled ↓ for download and ↑ for
  upload.
- About showed "Copyright Â©": the copyright in `Directory.Build.props` was stored double-encoded.
- On narrower windows the Speed test figures ran into each other, the Trends day header overlapped
  its legend, History's summary cards clipped, and Live's sockets and the Connections log squeezed
  their columns unreadably.
- The Trends day chart's tooltip was cut off at the bottom of the chart on days with many
  applications.
- A regional download that never started before the region's time ran out was stored as 0 Mbps; it
  is now stored as not measured, with the reason, so it no longer drags down comparisons.

- A network detail Windows refuses to give (the network name, metered status, signal strength,
  the system proxy) no longer makes the whole reading count as a disconnect, which logged a false
  "Disconnected" and "Connected" pair of events.
- Log files follow `Storage:DataDirectory` when it is set, so "Open logs" and the error dialog
  point at the folder the logs are actually in.

### Changed

- Lighter in the background: the network is re-read with one process snapshot instead of four,
  unchanged readings are no longer re-sent to every page, today's usage is read from the store
  once a minute instead of twice, the Network page's history chart is only rebuilt while the page
  is showing, and the speed test's upload buffer grows with the line instead of starting at 25 MB.

## [1.1.0] - 2026-09-14

The connection itself, not just who uses it.

### Added

- **Network page.** What this PC is connected to: network name (the name Windows gives it, such as a
  corporate domain, rather than just "Ethernet"), connection type, link speed, Wi-Fi signal,
  addresses, router, DNS servers and whether the connection is metered.
- **VPN and security proxy detection.** Netskope, Zscaler, Cloudflare WARP and Forcepoint are
  recognised by their running agent; FortiClient, GlobalProtect, Cisco Secure Client, WireGuard,
  OpenVPN, Tailscale and others by an adapter that is actually up. Pages that measure the
  connection say when results describe that path rather than the raw line.
- **Connection quality monitor.** Every 30 seconds: three pings to the router and three small HTTPS
  requests to Cloudflare. Reported as Good, Slow, Internet down or Offline with what that means in
  practice, and charted over the last hour, day or week. Distinguishes a local or Wi-Fi problem
  from an internet one. Runs every 5 minutes on metered connections; can be turned off in Settings.
- **Network events.** Disconnects, reconnects with how long the connection was down, network
  switches, captive portals, VPN or proxy starting and stopping, and internet-down and slow spells.
  Listed on the Network page and kept for a year.
- **Event markers on Trends.** Network events appear as labelled markers on the selected day's
  chart, with a count of the day's problems in its header.
- **Speed test page.** Download, upload, response time and jitter against Cloudflare's nearest data
  centre, with a live reading while it runs, a "what this connection is good for" verdict (browsing,
  HD calls, 4K streaming, gaming, large uploads, several streams) and a history table and chart.
  Asks before running on a metered connection.
- **Heavy-use alerts.** A tray notification when one application uses more than a chosen amount a
  minute (default 100 MB) for three minutes in a row, at most once an hour per application.
- **Data budgets.** Optional daily and monthly budgets with a billing-cycle reset day, progress bars
  on Live, and notifications at 80% and 100% that are not repeated after a restart.
- **Your connection card on Live**, beside the throughput chart: network, type, quality and any VPN
  or proxy, with a link to the Network page.
- **Connections page explanation**: what the page shows, how it is gathered, how to read a line and
  what is normal. Can be hidden and brought back with "What is this page?".
- **Service column on Connections**, naming each port's usual purpose (443 is Web (HTTPS), 3389 is
  Remote Desktop, and so on).
- **Settings › Monitoring and alerts** for the quality checks, heavy-use alerts and budgets.
- `Health` section in `appsettings.json` for the check interval, probe URL and thresholds;
  `HealthSampleDays` and `NetworkEventDays` under `Retention`.

### Changed

- The database schema moves to version 2 with three new tables (`NetworkEvents`, `HealthSamples`,
  `SpeedTests`). The upgrade is automatic and keeps existing history.
- Cybernetic Bandwidth Monitor now makes network requests of its own, limited to the quality checks and user-started
  speed tests. The About page and README say so.
- Sockets that Windows has handed to PID 0 while they wait out TimeWait are shown as
  "Closing connection" instead of "System Idle".
- Side rail gains Network and Speed test.

### Fixed

- **Open sockets always showed 0 and the Connections log stayed empty.** Reading the start time of a
  process the user may not open (antivirus, protected services) threw, and one such process
  discarded the entire connection table on every poll. Those processes are now listed by name.

## [1.0.0] - 2026-09-14

First versioned release, built on the original Cybernetic Bandwidth Monitor code.

### Added

- **System tray.** Cybernetic Bandwidth Monitor runs in the notification area with live download and upload rates in
  the tooltip. Closing the window hides it and collection continues; Exit on the tray menu stops it.
  Start-with-Windows launches straight to the tray. Starting Cybernetic Bandwidth Monitor again brings the running copy
  forward instead of showing an "already running" message.
- **Light and dark themes.** Follows the Windows app theme by default and switches when Windows
  does; Settings › Appearance and a toggle in the side rail can pin either. Covers the title bar,
  every control and all charts.
- **Today's total on Live**: download and upload since local midnight, including time before
  Cybernetic Bandwidth Monitor started.
- **Trends redesign.** A daily overview for 7 days to a year that can be zoomed, panned and clicked,
  and a detail chart of the selected day stacked by application at 1, 5 or 15 minutes or hourly,
  with a table of that day's applications to filter the chart.
- **About page** with what Cybernetic Bandwidth Monitor does, the author, and version, build, runtime and install
  details, plus "Copy details".
- **Application icon** in download and upload colours, generated by `build/make-icon.ps1` and used for
  the exe, window, taskbar, tray, Start Menu shortcut and Settings › Apps entry.
- Version, author and copyright in the exe's file properties; the installer registers the product
  version and Lance du Plessis as publisher.

### Changed

- The standard Windows title bar is kept (snap layouts and Aero Snap still work) and coloured to
  match the theme.
- Stored application names are re-derived on every start, so history recorded under an older name
  is relabelled.
- Log files no longer include every SQL statement; EF Core logs at Warning and above only.
- `Microsoft.Extensions.*` and `Microsoft.EntityFrameworkCore.*` updated from 10.0.0 to 10.0.12.

### Fixed

- **The solution did not build.** `Directory.Packages.props` contained `--` inside an XML comment, so
  MSBuild silently skipped it and no package versions resolved; `HostingExtensions.cs` was missing
  `System.IO`; the chart axes used `TextPaint`, which LiveCharts names `LabelsPaint`.
- **Every application was "Unattributed system traffic".** Windows only supplies application names
  to packaged apps. Names are now resolved from the attribution id: the exe's version information for
  desktop programs, the package manifest for Store apps, and "Windows (…)" for system traffic.
- **Usage was undercounted** when several connection profiles shared an interface type (Ethernet
  plus a Bluetooth network, several Wi-Fi networks): each profile overwrote the previous one's
  figure for the same minute instead of adding to it.
- **The title bar opened off the top of the screen** on displays shorter than the window's default
  height. The window now sizes itself to the monitor it opens on.
- A known high-severity vulnerability in the bundled SQLite library (`SQLitePCLRaw.lib.e_sqlite3`
  2.1.11), resolved by the EF Core update.
- The Trends chart legend was unreadable dark text on the dark background.
- Rollup and pruning SQL now passes values as parameters.

[Unreleased]: #unreleased
[1.1.0]: #110---2026-09-14
[1.0.0]: #100---2026-09-14
