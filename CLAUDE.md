## Project

NetTrace: a WPF (.NET 10) per-user network monitor for Windows that runs unelevated. `README.md` is the design reference: read the section for the area you are changing (storage, theming, the connection itself) before changing it.

- **Changelog**: every user-visible change gets an entry under `## [Unreleased]` in `CHANGELOG.md`, in the same change. Releases follow "Versioning and releases" in `README.md`.
- **Unelevated**: every data source must work for a standard user. A Windows API that can refuse (process access, Wi-Fi details, COM) degrades to a fallback value and never fails the whole read. The optional Windows service is the one elevated path: only `CollectorServiceSetup`'s `--install-service`/`--remove-service` step runs as administrator, and the collector must keep working without it. A new broker event needs a `CollectorLink` message too, or a window showing the service's data never gets it ("The Windows service" in `README.md`).
- **Theming**: colours come only from `Themes/Dark.xaml` and `Themes/Light.xaml` (same keys in both), referenced with `DynamicResource`. Chart paints come from `ThemeService.Chart` and are rebuilt on `ThemeChanged`.
- **Window sizes**: every page must work from 760 × 540 DIP up. Wrap page content in a ScrollViewer, set `ui:Layout.CompactBelow` on its root, and lay out side-by-side content with `AdaptiveColumns` and tiles with `AdaptiveGrid` (see "Look and feel, and window sizes" in `README.md`). Check a UI change at a narrow size as well as the default.
- **Glyphs**: Segoe Fluent icon characters are written as `\uXXXX` escapes in C# and `&#xXXXX;` entities in XAML, so they stay visible in source.
- **Verify in the app**: build with `dotnet build -c Release` and check UI changes in the running app, in both themes. A running NetTrace locks `bin`; exit it from the tray before rebuilding.

## Agent skills

### Issue tracker

Issues and specs live as local markdown files under `.scratch/<feature>/`. See `docs/agents/issue-tracker.md`.

### Triage labels

Default five-role vocabulary (`needs-triage`, `needs-info`, `ready-for-agent`, `ready-for-human`, `wontfix`), recorded as a `Status:` line in each issue file. See `docs/agents/triage-labels.md`.

### Domain docs

Single-context: one `CONTEXT.md` and `docs/adr/` at the repo root, created only when they're needed. See `docs/agents/domain.md`.
