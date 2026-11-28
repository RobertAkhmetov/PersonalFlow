using System.Globalization;
using PersonalFlow.Client;
using PersonalFlow.Core;

namespace PersonalFlow.Mobile.Reminders;

public enum ReminderMoment { OneHourBefore, AtTime }

public sealed record PlannedReminder(string Id, ReminderMoment Moment, DateTimeOffset At);

public sealed record ReminderStatus(bool NotificationsAllowed, bool ExactTimeAvailable, string Description);

public interface IReminderPlatform
{
    Task<ReminderStatus> GetStatusAsync();
    Task<ReminderStatus> RequestAccessAsync();
    Task ReconcileAsync(IReadOnlyList<PlannedReminder> reminders, CancellationToken cancellationToken = default);
}

public sealed class ReminderCoordinator(LocalStore store, IReminderPlatform platform)
{
    private static readonly TimeZoneInfo Moscow = TimeZoneInfo.FindSystemTimeZoneById("Europe/Moscow");
    private readonly SemaphoreSlim rebuildGate = new(1, 1);
    public event Action<ReminderStatus>? StatusChanged;

    public Task<ReminderStatus> GetStatusAsync() => platform.GetStatusAsync();

    public async Task<ReminderStatus> RequestAccessAsync()
    {
        await platform.RequestAccessAsync();
        await RebuildAsync();
        return await platform.GetStatusAsync();
    }

    public async Task RebuildAsync(CancellationToken cancellationToken = default)
    {
        await rebuildGate.WaitAsync(cancellationToken);
        try
        {
            var status = await platform.GetStatusAsync();
            var reminders = status.NotificationsAllowed ? BuildPlan() : [];
            await platform.ReconcileAsync(reminders, cancellationToken);
            StatusChanged?.Invoke(status);
        }
        finally
        {
            rebuildGate.Release();
        }
    }

    public void OnResume() => _ = ResumeAsync();

    private async Task ResumeAsync()
    {
        try { await RebuildAsync(); }
        catch (Exception) { /* The UI remains usable if the OS blocks notification rescheduling. */ }
    }

    private IReadOnlyList<PlannedReminder> BuildPlan()
    {
        var today = LocalStore.Today;
        var through = today.AddDays(60);
        var plans = store.All<PlanItem>();
        var recurring = RecurrenceProjection.Expand(today, through,
            store.All<Recurrence>(), store.All<Vacation>(), store.All<OccurrenceOverride>());
        var entries = plans.Where(x => x.Date >= today && x.Date <= through)
            .Select(x => new PlannedEntry(x.Id, x.Title, x.Date, x.Done, x.Time))
            .Concat(recurring)
            .Where(x => !x.Done && !string.IsNullOrWhiteSpace(x.Time))
            .Select(x => (Entry: x, Time: ParseTime(x.Time!)))
            .Where(x => x.Time is not null)
            .OrderBy(x => x.Entry.Date).ThenBy(x => x.Time)
            .Take(30) // iOS allows at most 64 pending requests; each timed item creates two.
            .ToArray();

        var result = new List<PlannedReminder>(entries.Length * 2);
        foreach (var (entry, time) in entries)
        {
            var local = DateTime.SpecifyKind(entry.Date.ToDateTime(time!.Value), DateTimeKind.Unspecified);
            var utc = TimeZoneInfo.ConvertTimeToUtc(local, Moscow);
            var at = new DateTimeOffset(utc, TimeSpan.Zero);
            result.Add(new PlannedReminder($"{entry.Id:N}:before", ReminderMoment.OneHourBefore, at.AddHours(-1)));
            result.Add(new PlannedReminder($"{entry.Id:N}:at", ReminderMoment.AtTime, at));
        }
        return result.Where(x => x.At > DateTimeOffset.UtcNow).OrderBy(x => x.At).ToArray();
    }

    private static TimeOnly? ParseTime(string value) => TimeOnly.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.None, out var parsed) ? parsed : null;
}
