using System.Globalization;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using CyberneticBandwidth.Core.Abstractions;
using CyberneticBandwidth.Core.Models;
using CyberneticBandwidth.Core.Services;

namespace CyberneticBandwidth.App.Services;

/// <summary>
/// Usage against the user's budgets, as of the last check. Today's total is read whether or not a
/// budget is set: the Live page shows it from here rather than querying the store a second time.
/// </summary>
public sealed record BudgetStatus(
    long TodayBytesSent, long TodayBytesReceived, double? DailyBudgetBytes, long PeriodBytes, double? MonthlyBudgetBytes, DateOnly PeriodStart)
{
    public long TodayBytes => TodayBytesSent + TodayBytesReceived;

    public double? DailyFraction => DailyBudgetBytes is > 0 ? TodayBytes / DailyBudgetBytes : null;
    public double? MonthlyFraction => MonthlyBudgetBytes is > 0 ? PeriodBytes / MonthlyBudgetBytes : null;
}

/// <summary>
/// Two kinds of heads-up, both driven by the per-minute usage the sampler publishes:
///  - one application using a lot for several minutes in a row (a cloud backup, an update, a
///    stream left running), so the user can see what is eating the connection;
///  - the day's or the billing month's total crossing 80% and 100% of a budget.
/// Each fires once: per application per hour, and per budget level per period.
/// </summary>
public sealed class UsageAlertService
{
    public const int SustainedMinutes = 3;
    private static readonly TimeSpan HogCooldown = TimeSpan.FromHours(1);
    private static readonly double[] BudgetLevels = [1.0, 0.8];

    private const double Megabyte = ByteFormat.Megabyte;
    private const double Gigabyte = ByteFormat.Gigabyte;

    private readonly IServiceScopeFactory _scopes;
    private readonly PreferencesStore _preferences;
    private readonly NotificationCenter _notifications;
    private readonly ILogger<UsageAlertService> _log;

    // Per app, the latest total seen for each minute bucket; re-reads replace, never add.
    private readonly Dictionary<string, SortedDictionary<long, long>> _minutes = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> _names = new(StringComparer.Ordinal);
    private readonly Dictionary<string, DateTimeOffset> _lastHogAlert = new(StringComparer.Ordinal);
    private bool _checkingBudget;

    public BudgetStatus? Latest { get; private set; }

    public event Action<BudgetStatus>? BudgetUpdated;

    public UsageAlertService(
        LiveActivityBroker broker,
        IServiceScopeFactory scopes,
        PreferencesStore preferences,
        NotificationCenter notifications,
        ILogger<UsageAlertService> log)
    {
        _scopes = scopes;
        _preferences = preferences;
        _notifications = notifications;
        _log = log;

        broker.UsageUpdated += OnUsage;
        _ = CheckBudgetsAsync();
    }

    /// <summary>For Settings: budgets changed, so re-evaluate now rather than at the next sample.</summary>
    public Task RefreshAsync() => CheckBudgetsAsync();

    private void OnUsage(IReadOnlyList<UsageSample> samples)
    {
        if (_preferences.Current.HogAlertsEnabled) CheckHogs(samples);
        _ = CheckBudgetsAsync();
    }

    private void CheckHogs(IReadOnlyList<UsageSample> samples)
    {
        foreach (var group in samples.Where(s => s.Granularity == Granularity.Minute)
                                     .GroupBy(s => (s.AttributionId, Bucket: s.BucketStartUtc.ToUnixTimeSeconds())))
        {
            if (!_minutes.TryGetValue(group.Key.AttributionId, out var buckets))
                _minutes[group.Key.AttributionId] = buckets = new SortedDictionary<long, long>();

            buckets[group.Key.Bucket] = group.Sum(s => s.Total);
            _names[group.Key.AttributionId] = group.First().DisplayName;
        }

        double threshold = _preferences.Current.HogThresholdMegabytesPerMinute * Megabyte;
        long currentMinute = DateTimeOffset.UtcNow.FloorTo(Granularity.Minute).ToUnixTimeSeconds();
        var now = DateTimeOffset.UtcNow;

        foreach (var (app, buckets) in _minutes)
        {
            // Drop what can no longer matter: anything older than the window plus a margin.
            foreach (var old in buckets.Keys.Where(k => k < currentMinute - 30 * 60).ToList()) buckets.Remove(old);

            // The last N minutes that have finished; the current one is still filling.
            var recent = Enumerable.Range(1, SustainedMinutes)
                .Select(i => buckets.GetValueOrDefault(currentMinute - i * 60L))
                .ToList();

            if (recent.Any(bytes => bytes < threshold)) continue;
            if (_lastHogAlert.TryGetValue(app, out var last) && now - last < HogCooldown) continue;

            _lastHogAlert[app] = now;
            string name = _names.GetValueOrDefault(app, "An application");
            _notifications.Notify(
                $"{name} is using a lot of data",
                $"About {ByteFormat.Size(recent.Average())} a minute for the last {SustainedMinutes} minutes. " +
                "Open Cybernetic Bandwidth Monitor to see what else is using your connection.");
        }
    }

    private async Task CheckBudgetsAsync()
    {
        if (_checkingBudget) return;
        _checkingBudget = true;

        try
        {
            var prefs = _preferences.Current;
            var today = LocalDays.Today;
            var periodStart = PeriodStart(today, prefs.BudgetResetDay);

            await using var scope = _scopes.CreateAsyncScope();
            var repo = scope.ServiceProvider.GetRequiredService<IUsageRepository>();

            var todayPoints = await repo.GetTimelineAsync(LocalDays.StartOf(today), LocalDays.EndOf(today), TimeSpan.FromHours(1), CancellationToken.None);
            long periodBytes = prefs.MonthlyBudgetGigabytes is null
                ? 0
                : (await repo.GetTimelineAsync(LocalDays.StartOf(periodStart), LocalDays.EndOf(today), TimeSpan.FromHours(1), CancellationToken.None))
                    .Sum(p => p.BytesSent + p.BytesReceived);

            var status = new BudgetStatus(
                todayPoints.Sum(p => p.BytesSent), todayPoints.Sum(p => p.BytesReceived), prefs.DailyBudgetGigabytes * Gigabyte,
                periodBytes, prefs.MonthlyBudgetGigabytes * Gigabyte,
                periodStart);

            Latest = status;
            BudgetUpdated?.Invoke(status);

            bool changed = false;
            changed |= WarnOnce(status.DailyFraction, $"{today:yyyy-MM-dd}", prefs.DailyBudgetAlerted, v => prefs.DailyBudgetAlerted = v,
                level => ($"{(level >= 1 ? "Daily data budget used up" : "80% of today's data budget used")}",
                          $"{ByteFormat.Size(status.TodayBytes)} of {ByteFormat.Size(status.DailyBudgetBytes ?? 0)} today."));
            changed |= WarnOnce(status.MonthlyFraction, $"{periodStart:yyyy-MM-dd}", prefs.MonthlyBudgetAlerted, v => prefs.MonthlyBudgetAlerted = v,
                level => ($"{(level >= 1 ? "Monthly data budget used up" : "80% of this month's data budget used")}",
                          $"{ByteFormat.Size(periodBytes)} of {ByteFormat.Size(status.MonthlyBudgetBytes ?? 0)} since {periodStart:d MMMM}."));

            if (changed) _preferences.Save();
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Could not check usage budgets.");
        }
        finally
        {
            _checkingBudget = false;
        }
    }

    /// <summary>
    /// Remembers "period|level" once warned, so a restart does not repeat the 80% warning and the
    /// 100% one can still follow it.
    /// </summary>
    private bool WarnOnce(double? fraction, string period, string? alerted, Action<string> remember,
        Func<double, (string Title, string Message)> text)
    {
        if (fraction is not { } f) return false;

        double reached = BudgetLevels.FirstOrDefault(level => f >= level);
        double already = alerted is not null && alerted.StartsWith(period + "|", StringComparison.Ordinal)
                         && double.TryParse(alerted[(period.Length + 1)..], NumberStyles.Float, CultureInfo.InvariantCulture, out double level)
            ? level
            : 0;

        if (reached <= already) return false;

        var (title, message) = text(reached);
        _notifications.Notify(title, message);
        remember($"{period}|{reached.ToString(CultureInfo.InvariantCulture)}");
        return true;
    }

    /// <summary>The most recent reset day on or before today. Reset days stop at 28, which every month has.</summary>
    public static DateOnly PeriodStart(DateOnly today, int resetDay)
    {
        DateOnly Clamp(int year, int month) => new(year, month, Math.Clamp(resetDay, 1, 28));

        var thisMonth = Clamp(today.Year, today.Month);
        if (thisMonth <= today) return thisMonth;

        var previous = today.AddMonths(-1);
        return Clamp(previous.Year, previous.Month);
    }
}
