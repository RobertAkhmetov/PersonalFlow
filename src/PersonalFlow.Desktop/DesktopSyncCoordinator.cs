using System.Net.Http.Json;
using System.Text.Json.Nodes;
using PersonalFlow.Client;

namespace PersonalFlow.Desktop;

internal sealed record DesktopSyncResult(int LessonsAdded);

internal sealed class DesktopSyncCoordinator(LocalStore store, DesktopIdentitySession identity)
{
    readonly SemaphoreSlim synchronizationGate = new(1, 1);

    public async Task<DesktopSyncResult> SynchronizeAsync(CancellationToken cancellationToken = default)
    {
        if (!identity.IsConfigured) throw new InvalidOperationException("Сначала настройте адреса сервисов.");
        if (!await identity.HasSessionAsync(cancellationToken)) throw new InvalidOperationException("Сначала войдите в аккаунт.");

        await synchronizationGate.WaitAsync(cancellationToken);
        try
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
        finally { synchronizationGate.Release(); }
    }
}
