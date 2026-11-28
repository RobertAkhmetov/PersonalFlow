using UserNotifications;
using Microsoft.Maui.ApplicationModel;
using PersonalFlow.Mobile.Reminders;

namespace PersonalFlow.Mobile;

public sealed class IosReminderPlatform : IReminderPlatform
{
    private const string Prefix = "personalflow:";
    private readonly UNUserNotificationCenter center = UNUserNotificationCenter.Current;
    private readonly PersonalFlowNotificationDelegate notificationDelegate;

    public IosReminderPlatform()
    {
        notificationDelegate = new PersonalFlowNotificationDelegate();
        center.Delegate = notificationDelegate;
    }

    public async Task<ReminderStatus> GetStatusAsync()
    {
        var settings = await center.GetNotificationSettingsAsync();
        var allowed = settings.AuthorizationStatus is UNAuthorizationStatus.Authorized
            or UNAuthorizationStatus.Provisional or UNAuthorizationStatus.Ephemeral;
        return new ReminderStatus(allowed, allowed,
            allowed
                ? "Напоминания разрешены: за час и в указанное время."
                : "Разрешите уведомления, чтобы получать локальные напоминания.");
    }

    public async Task<ReminderStatus> RequestAccessAsync()
    {
        var existing = await center.GetNotificationSettingsAsync();
        if (existing.AuthorizationStatus == UNAuthorizationStatus.Denied)
        {
            AppInfo.ShowSettingsUI();
            return await GetStatusAsync();
        }
        await center.RequestAuthorizationAsync(UNAuthorizationOptions.Alert | UNAuthorizationOptions.Sound);
        return await GetStatusAsync();
    }

    public async Task ReconcileAsync(IReadOnlyList<PlannedReminder> reminders, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var pending = await center.GetPendingNotificationRequestsAsync();
        var obsolete = pending.Where(x => x.Identifier.StartsWith(Prefix, StringComparison.Ordinal))
            .Select(x => x.Identifier).ToArray();
        if (obsolete.Length > 0) center.RemovePendingNotificationRequests(obsolete);

        var status = await GetStatusAsync();
        if (!status.NotificationsAllowed) return;
        foreach (var reminder in reminders.OrderBy(x => x.At).Take(60))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var seconds = (reminder.At - DateTimeOffset.UtcNow).TotalSeconds;
            if (seconds < 1) continue;
            var content = new UNMutableNotificationContent
            {
                Title = "PersonalFlow",
                Body = reminder.Moment == ReminderMoment.OneHourBefore
                    ? "Дело запланировано через час"
                    : "Наступило время дела",
                Sound = UNNotificationSound.Default
            };
            var trigger = UNTimeIntervalNotificationTrigger.CreateTrigger(seconds, false);
            var request = UNNotificationRequest.FromIdentifier($"{Prefix}{reminder.Id}", content, trigger);
            await center.AddNotificationRequestAsync(request);
        }
    }
}

internal sealed class PersonalFlowNotificationDelegate : UNUserNotificationCenterDelegate
{
    public override void WillPresentNotification(UNUserNotificationCenter center, UNNotification notification,
        Action<UNNotificationPresentationOptions> completionHandler) =>
        completionHandler(UNNotificationPresentationOptions.Banner | UNNotificationPresentationOptions.Sound);
}
