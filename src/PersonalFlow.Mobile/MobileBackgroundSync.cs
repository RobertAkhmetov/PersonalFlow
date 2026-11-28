using Microsoft.Maui.Networking;
using Microsoft.Maui.Storage;
using PersonalFlow.Client;
using PersonalFlow.Mobile.Reminders;

namespace PersonalFlow.Mobile;

public sealed record MobileBackgroundSyncStatus(bool IsRunning, DateTimeOffset? LastSuccessfulSync, string? Notice)
{
    public string StateLabel => IsRunning
        ? "Выполняется…"
        : !string.IsNullOrWhiteSpace(Notice)
            ? "Ожидает повтора"
            : LastSuccessfulSync is not null ? "Успешно" : "Ожидает первого обмена";

    public string LastSyncLabel => LastSuccessfulSync is { } value
        ? $"Последняя синхронизация: {value.ToLocalTime():d MMM yyyy, HH:mm}"
        : "Синхронизация ещё не выполнялась";
}

public sealed class MobileBackgroundSync(
    MobileIdentitySession identity,
    MobileSyncCoordinator synchronizer,
    LocalStore store,
    ReminderCoordinator reminders)
{
    const string LastSuccessfulSyncSetting = "personalflow.sync.last-success";
    static readonly TimeSpan MinimumAttemptInterval = TimeSpan.FromMinutes(2);
    readonly SemaphoreSlim gate = new(1, 1);
    DateTimeOffset? lastAttempt;
    MobileBackgroundSyncStatus status = new(false, ReadLastSuccessfulSync(), null);

    public MobileBackgroundSyncStatus Status => status;
    public event Action<MobileBackgroundSyncStatus>? StatusChanged;

    public async Task TrySynchronizeAsync()
    {
        if (!await gate.WaitAsync(0))
            return;

        try
        {
            if (!identity.IsConfigured || !await identity.HasSessionAsync())
            {
                SetStatus(status with { IsRunning = false, Notice = null });
                return;
            }

            if (Connectivity.Current.NetworkAccess != NetworkAccess.Internet)
            {
                SetStatus(status with { IsRunning = false, Notice = "Нет сети. Изменения останутся на устройстве." });
                return;
            }

            var now = DateTimeOffset.UtcNow;
            if (lastAttempt is { } previous && now - previous < MinimumAttemptInterval)
                return;
            lastAttempt = now;

            SetStatus(status with { IsRunning = true, Notice = null });
            try
            {
                await synchronizer.SynchronizeAsync();
                try { await reminders.RebuildAsync(); } catch (Exception) { }
                RecordSuccessfulSynchronization();
            }
            catch (Exception)
            {
                var pending = store.SyncStatus.Pending;
                var notice = pending > 0
                    ? $"Автоматическая синхронизация не завершена. {pending} изменений сохранены на устройстве."
                    : "Автоматическая синхронизация не завершена. Повторим попытку при следующем открытии.";
                SetStatus(status with { IsRunning = false, Notice = notice });
            }
        }
        finally
        {
            gate.Release();
        }
    }

    public void RecordSuccessfulSynchronization()
    {
        var completed = DateTimeOffset.UtcNow;
        Preferences.Default.Set(LastSuccessfulSyncSetting, completed.ToString("O"));
        SetStatus(new(false, completed, null));
    }

    void SetStatus(MobileBackgroundSyncStatus value)
    {
        status = value;
        try { StatusChanged?.Invoke(value); } catch (Exception) { }
    }

    static DateTimeOffset? ReadLastSuccessfulSync()
    {
        var value = Preferences.Default.Get(LastSuccessfulSyncSetting, string.Empty);
        return DateTimeOffset.TryParse(value, out var parsed) ? parsed : null;
    }
}
