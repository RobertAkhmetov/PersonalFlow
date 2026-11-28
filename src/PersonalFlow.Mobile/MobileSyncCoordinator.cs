using PersonalFlow.Client;
using System.Text.Json.Nodes;

namespace PersonalFlow.Mobile;

public sealed record MobileSyncResult(int LessonsAdded);

public sealed class MobileSyncCoordinator(LocalStore store, MobileIdentitySession identity)
{
    readonly SemaphoreSlim synchronizationGate = new(1, 1);

    public async Task<MobileSyncResult> SynchronizeAsync(CancellationToken cancellationToken = default)
    {
        await EnsureSignedInAsync();
        await synchronizationGate.WaitAsync(cancellationToken);
        try
        {
            return await SynchronizeCoreAsync(cancellationToken);
        }
        finally { synchronizationGate.Release(); }
    }

    public async Task<MobileSyncResult> RecoverEpochsAsync(CancellationToken cancellationToken = default)
    {
        await EnsureSignedInAsync();
        await synchronizationGate.WaitAsync(cancellationToken);
        try
        {
            var conflicts = store.GetSyncEpochConflicts();
            if (conflicts.Count == 0)
                throw new InvalidOperationException("Смена эпохи сервера не ожидает восстановления.");

            foreach (var conflict in conflicts)
            {
                if (conflict.Service == "life")
                {
                    using var client = identity.CreateApiClient(identity.LifeApi);
                    var state = await client.GetFromJsonAsync<SyncEpochState>("v1/sync/state", cancellationToken)
                        ?? throw new InvalidDataException("Сервис планов не вернул текущую эпоху.");
                    store.BeginSyncEpochRecovery("life", state.Epoch);
                }
                else if (conflict.Service == "learning")
                {
                    using var client = identity.CreateApiClient(identity.LearningApi);
                    var state = await client.GetFromJsonAsync<SyncEpochState>("v1/sync/state", cancellationToken)
                        ?? throw new InvalidDataException("Сервис обучения не вернул текущую эпоху.");
                    store.BeginSyncEpochRecovery("learning", state.Epoch);
                }
            }

            return await SynchronizeCoreAsync(cancellationToken);
        }
        finally { synchronizationGate.Release(); }
    }

    async Task EnsureSignedInAsync()
    {
        if (!identity.IsConfigured)
            throw new InvalidOperationException("Сначала настройте адреса сервисов.");
        if (!await identity.HasSessionAsync())
            throw new InvalidOperationException("Сначала войдите в аккаунт.");
    }

    async Task<MobileSyncResult> SynchronizeCoreAsync(CancellationToken cancellationToken)
    {
        var errors = new List<string>();
        try
        {
            using var client = identity.CreateApiClient(identity.LifeApi);
            await store.SynchronizeLifeAsync(client, cancellationToken);
            var conflicts = await client.GetFromJsonAsync<JsonArray>("v1/conflicts", cancellationToken)
                ?? throw new InvalidDataException("Сервер не вернул список конфликтов планов.");
            store.ReplaceRemoteConflictSnapshot("life", conflicts.ToJsonString());
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch { errors.Add("Планы и заметки"); }

        var addedLessons = 0;
        try
        {
            using var client = identity.CreateApiClient(identity.LearningApi);
            await store.SynchronizeLearningAsync(client, cancellationToken);
            addedLessons = await store.DownloadLessonsAsync(client, cancellationToken: cancellationToken);
            var conflicts = await client.GetFromJsonAsync<JsonArray>("v1/progress/conflicts", cancellationToken)
                ?? throw new InvalidDataException("Сервер не вернул список конфликтов обучения.");
            store.ReplaceRemoteConflictSnapshot("learning", conflicts.ToJsonString());
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch { errors.Add("Обучение"); }

        if (errors.Count > 0)
            throw new InvalidOperationException("Не удалось синхронизировать разделы: " + string.Join(", ", errors) + ". Локальные изменения сохранены.");
        return new(addedLessons);
    }

    sealed record SyncEpochState(Guid Epoch);
}
