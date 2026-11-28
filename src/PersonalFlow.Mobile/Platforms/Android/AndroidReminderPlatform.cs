using System.Security.Cryptography;
using System.Runtime.Versioning;
using System.Text;
using System.Text.Json;
using Android.App;
using Android.Content;
using Android.OS;
using Android.Provider;
using AndroidX.Core.App;
using Microsoft.Maui.ApplicationModel;
using PersonalFlow.Mobile.Reminders;

namespace PersonalFlow.Mobile;

public sealed class AndroidReminderPlatform : IReminderPlatform
{
    private const string ChannelId = "personalflow.tasks";
    private const string PreferencesName = "personalflow.reminders";
    private const string QueueKey = "queue";
    private const string NotificationPermissionAskedKey = "notification_permission_asked";
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private static readonly object Gate = new();
    private readonly Context context = Android.App.Application.Context;
    [SupportedOSPlatformGuard("android31.0")]
    private static bool SupportsExactAlarmPermission => Build.VERSION.SdkInt >= BuildVersionCodes.S;
    [SupportedOSPlatformGuard("android26.0")]
    private static bool SupportsNotificationChannels => Build.VERSION.SdkInt >= BuildVersionCodes.O;

    public Task<ReminderStatus> GetStatusAsync() => Task.FromResult(ReadStatus());

    public async Task<ReminderStatus> RequestAccessAsync()
    {
        if (Build.VERSION.SdkInt >= BuildVersionCodes.Tiramisu)
        {
            var result = await Permissions.RequestAsync<Permissions.PostNotifications>();
            var preferences = context.GetSharedPreferences(PreferencesName, FileCreationMode.Private);
            var wasAsked = preferences?.GetBoolean(NotificationPermissionAskedKey, false) == true;
            preferences?.Edit()?.PutBoolean(NotificationPermissionAskedKey, true)?.Apply();
            if (result != PermissionStatus.Granted && wasAsked) AppInfo.ShowSettingsUI();
        }

        var alarmManager = context.GetSystemService(Context.AlarmService) as AlarmManager;
        if (NotificationManagerCompat.From(context)?.AreNotificationsEnabled() == true &&
            SupportsExactAlarmPermission && alarmManager is not null && !alarmManager.CanScheduleExactAlarms())
        {
            var intent = new Intent(Settings.ActionRequestScheduleExactAlarm);
            intent.SetData(Android.Net.Uri.Parse($"package:{context.PackageName}"));
            intent.AddFlags(ActivityFlags.NewTask);
            try { context.StartActivity(intent); }
            catch (ActivityNotFoundException) { }
        }
        return ReadStatus();
    }

    public Task ReconcileAsync(IReadOnlyList<PlannedReminder> reminders, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (Gate)
        {
            var previous = ReadQueue();
            foreach (var item in previous) Cancel(item.Id);

            var status = ReadStatus();
            var active = status.NotificationsAllowed
                ? reminders.Where(x => x.At > DateTimeOffset.UtcNow).ToArray()
                : [];
            EnsureChannel();
            foreach (var item in active) Schedule(item, status.ExactTimeAvailable);
            WriteQueue(active);
        }
        return Task.CompletedTask;
    }

    internal static void RestoreAfterBoot(Context context)
    {
        var queue = context.GetSharedPreferences(PreferencesName, FileCreationMode.Private)?
            .GetString(QueueKey, "[]") ?? "[]";
        var reminders = JsonSerializer.Deserialize<PlannedReminder[]>(queue, Json) ?? [];
        if (NotificationManagerCompat.From(context)?.AreNotificationsEnabled() != true) return;
        var alarmManager = context.GetSystemService(Context.AlarmService) as AlarmManager;
        var exact = !SupportsExactAlarmPermission || alarmManager?.CanScheduleExactAlarms() == true;
        EnsureChannel(context);
        foreach (var item in reminders.Where(x => x.At > DateTimeOffset.UtcNow)) Schedule(context, item, exact);
    }

    internal static void ShowNotification(Context context, string requestId, ReminderMoment moment)
    {
        if (NotificationManagerCompat.From(context)?.AreNotificationsEnabled() != true) return;
        EnsureChannel(context);
        var manager = context.GetSystemService(Context.NotificationService) as NotificationManager;
        if (manager is null) return;

        var contentIntent = context.PackageManager?.GetLaunchIntentForPackage(context.PackageName!);
        PendingIntent? pending = null;
        if (contentIntent is not null)
            pending = PendingIntent.GetActivity(context, RequestCode($"open:{requestId}"), contentIntent,
                PendingIntentFlags.UpdateCurrent | PendingIntentFlags.Immutable);

#pragma warning disable CA1416, CA1422, CS0618
        var builder = SupportsNotificationChannels
            ? new Notification.Builder(context, ChannelId)
            : new Notification.Builder(context);
#pragma warning restore CA1416, CA1422, CS0618
        builder.SetSmallIcon(Resource.Drawable.notification_small)
            .SetContentTitle("PersonalFlow")
            .SetContentText(moment == ReminderMoment.OneHourBefore ? "Дело запланировано через час" : "Наступило время дела")
            .SetAutoCancel(true)
            .SetOnlyAlertOnce(true);
        if (pending is not null) builder.SetContentIntent(pending);
        manager.Notify(RequestCode(requestId), builder.Build());
    }

    internal static void Schedule(Context context, PlannedReminder reminder, bool exact)
    {
        var alarmManager = context.GetSystemService(Context.AlarmService) as AlarmManager;
        if (alarmManager is null) return;
        var pending = CreateAlarmPendingIntent(context, reminder);
        var trigger = reminder.At.ToUnixTimeMilliseconds();
        try
        {
            if (exact && Build.VERSION.SdkInt >= BuildVersionCodes.M)
                alarmManager.SetExactAndAllowWhileIdle(AlarmType.RtcWakeup, trigger, pending);
            else
                alarmManager.SetAndAllowWhileIdle(AlarmType.RtcWakeup, trigger, pending);
        }
        catch (Java.Lang.SecurityException)
        {
            alarmManager.SetAndAllowWhileIdle(AlarmType.RtcWakeup, trigger, pending);
        }
    }

    private void Schedule(PlannedReminder reminder, bool exact) => Schedule(context, reminder, exact);

    private void Cancel(string id)
    {
        var pending = PendingIntent.GetBroadcast(context, RequestCode(id), AlarmIntent(context, id),
            PendingIntentFlags.NoCreate | PendingIntentFlags.UpdateCurrent | PendingIntentFlags.Immutable);
        if (pending is not null)
        {
            (context.GetSystemService(Context.AlarmService) as AlarmManager)?.Cancel(pending);
            pending.Cancel();
        }
        NotificationManagerCompat.From(context)?.Cancel(RequestCode(id));
    }

    private static PendingIntent CreateAlarmPendingIntent(Context context, PlannedReminder reminder)
    {
        var intent = AlarmIntent(context, reminder.Id);
        intent.PutExtra("moment", (int)reminder.Moment);
        return PendingIntent.GetBroadcast(context, RequestCode(reminder.Id), intent,
            PendingIntentFlags.UpdateCurrent | PendingIntentFlags.Immutable)
            ?? throw new InvalidOperationException("Android не создал PendingIntent для напоминания.");
    }

    private static Intent AlarmIntent(Context context, string id)
    {
        var intent = new Intent(context, typeof(ReminderReceiver));
        intent.SetAction($"app.personalflow.reminder.{id}");
        intent.PutExtra("requestId", id);
        return intent;
    }

    private ReminderStatus ReadStatus()
    {
        var allowed = NotificationManagerCompat.From(context)?.AreNotificationsEnabled() == true;
        var alarmManager = context.GetSystemService(Context.AlarmService) as AlarmManager;
        var exact = !SupportsExactAlarmPermission || alarmManager?.CanScheduleExactAlarms() == true;
        var description = !allowed
            ? "Разрешите уведомления, чтобы получать локальные напоминания."
            : exact
                ? "Уведомления будут приходить за час и в указанное время."
                : "Уведомления включены, но Android может задержать их до разрешения точного времени.";
        return new ReminderStatus(allowed, exact, description);
    }

    private void EnsureChannel() => EnsureChannel(context);

    private static void EnsureChannel(Context context)
    {
        if (!SupportsNotificationChannels) return;
        var manager = context.GetSystemService(Context.NotificationService) as NotificationManager;
        manager?.CreateNotificationChannel(new NotificationChannel(ChannelId, "Напоминания PersonalFlow", NotificationImportance.Default)
        {
            Description = "Напоминания о запланированных делах"
        });
    }

    private PlannedReminder[] ReadQueue()
    {
        var value = context.GetSharedPreferences(PreferencesName, FileCreationMode.Private)?.GetString(QueueKey, "[]") ?? "[]";
        return JsonSerializer.Deserialize<PlannedReminder[]>(value, Json) ?? [];
    }

    private void WriteQueue(IReadOnlyList<PlannedReminder> reminders) =>
        context.GetSharedPreferences(PreferencesName, FileCreationMode.Private)?.Edit()?.PutString(QueueKey, JsonSerializer.Serialize(reminders, Json))?.Commit();

    private static int RequestCode(string id)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(id));
        return BitConverter.ToInt32(bytes, 0) & int.MaxValue;
    }
}

[BroadcastReceiver(Enabled = true, Exported = false)]
public sealed class ReminderReceiver : BroadcastReceiver
{
    public override void OnReceive(Context? context, Intent? intent)
    {
        if (context is null || intent is null) return;
        var requestId = intent.GetStringExtra("requestId");
        if (string.IsNullOrWhiteSpace(requestId)) return;
        var moment = (ReminderMoment)intent.GetIntExtra("moment", (int)ReminderMoment.AtTime);
        AndroidReminderPlatform.ShowNotification(context, requestId, moment);
    }
}

[BroadcastReceiver(Enabled = true, Exported = false)]
[IntentFilter([Intent.ActionBootCompleted, Intent.ActionMyPackageReplaced])]
public sealed class ReminderBootReceiver : BroadcastReceiver
{
    public override void OnReceive(Context? context, Intent? intent)
    {
        if (context is not null) AndroidReminderPlatform.RestoreAfterBoot(context);
    }
}
