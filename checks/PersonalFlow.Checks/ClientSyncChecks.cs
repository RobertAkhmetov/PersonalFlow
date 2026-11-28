using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using PersonalFlow.Client;

internal static class ClientSyncChecks
{
    public static async Task Run()
    {
        var path = Path.Combine(Path.GetTempPath(), "personalflow-sync-" + Guid.NewGuid(), "client.db");
        var store = new LocalStore(path);
        var id = Guid.NewGuid();
        var item = new PlanItem(id, "Synthetic", new(2027, 1, 1));
        store.Save(id, item);
        store.Save(id, item with { Done = true });
        string? firstWire = null;
        var epoch = Guid.NewGuid();
        var calls = 0;
        using var http = new HttpClient(new Handler(async request =>
        {
            if (request.Method == HttpMethod.Get && request.RequestUri!.AbsolutePath.EndsWith("/sync/state", StringComparison.Ordinal)) return Json($"{{\"epoch\":\"{epoch:D}\"}}");
            if (request.Method == HttpMethod.Get) return Json($"{{\"items\":[],\"cursor\":0,\"epoch\":\"{epoch:D}\"}}");
            var raw = await request.Content!.ReadAsStringAsync();
            if (calls++ == 0) { firstWire = raw; throw new HttpRequestException("Simulated lost response"); }
            if (calls == 2 && firstWire != raw) throw new Exception("Retry changed frozen request.");
            var wire = JsonNode.Parse(raw)!;
            if (calls == 3)
            {
                if (wire["baseRevision"]!.GetValue<long>() != 1) throw new Exception("Dependent edit was not rebased after acknowledgment.");
                if (wire["patch"]!.AsObject().Count != 1 || wire["patch"]!["Done"]?.GetValue<bool>() != true) throw new Exception("Snapshot overwrite instead of field patch.");
            }
            var body = calls == 2 ? System.Text.Json.JsonSerializer.Serialize(item) : System.Text.Json.JsonSerializer.Serialize(item with { Done = true });
            return Json($"{{\"status\":\"applied\",\"revision\":{calls - 1},\"body\":{body},\"deleted\":false}}");
        })) { BaseAddress = new("https://synthetic.invalid/") };
        try { await store.SynchronizeLifeAsync(http); throw new Exception("Expected network failure."); } catch (HttpRequestException) { }
        if (store.Pending != 2) throw new Exception("Network failure lost queued edits.");
        store = new LocalStore(path);
        await store.SynchronizeLifeAsync(http);
        if (store.Pending != 0 || !store.All<PlanItem>().Single().Done) throw new Exception("Acknowledgment lost latest edit.");

        store.Save(id, item with { Title = "Local" });
        var other = Guid.NewGuid(); store.Save(other, new PlanItem(other, "Independent", item.Date));
        using var conflicts = new HttpClient(new Handler(async request =>
        {
            if (request.Method == HttpMethod.Get && request.RequestUri!.AbsolutePath.EndsWith("/sync/state", StringComparison.Ordinal)) return Json($"{{\"epoch\":\"{epoch:D}\"}}");
            if (request.Method == HttpMethod.Get) return Json($"{{\"items\":[],\"cursor\":0,\"epoch\":\"{epoch:D}\"}}");
            var wire = JsonNode.Parse(await request.Content!.ReadAsStringAsync())!;
            if (wire["entityId"]!.GetValue<string>() == id.ToString()) return Json("{\"status\":\"conflict\",\"conflict\":{\"id\":\"synthetic\"}}");
            return Json($"{{\"status\":\"applied\",\"revision\":3,\"body\":{wire["patch"]!.ToJsonString()},\"deleted\":false}}");
        })) { BaseAddress = new("https://synthetic.invalid/") };
        await store.SynchronizeLifeAsync(conflicts);
        if (store.Pending != 1 || new LocalStore(path).SyncConflicts().Count != 1 || store.SyncStatus.Conflicts != 1) throw new Exception("Conflict blocked independent entity or was not durable.");

        var epochPath = Path.Combine(Path.GetTempPath(), "personalflow-epoch-" + Guid.NewGuid(), "client.db");
        var epochStore = new LocalStore(epochPath);
        var epochItemId = Guid.NewGuid();
        epochStore.Save(epochItemId, new PlanItem(epochItemId, "Epoch check", item.Date));
        var rotatedEpoch = Guid.NewGuid();
        using var rotatedServer = new HttpClient(new Handler(async request =>
        {
            if (request.Method == HttpMethod.Get && request.RequestUri!.AbsolutePath.EndsWith("/sync/state", StringComparison.Ordinal))
                return Json($"{{\"epoch\":\"{epoch:D}\"}}");
            if (request.Method == HttpMethod.Post)
            {
                var wire = JsonNode.Parse(await request.Content!.ReadAsStringAsync())!;
                return Json($"{{\"status\":\"applied\",\"revision\":1,\"body\":{wire["patch"]!.ToJsonString()},\"deleted\":false}}");
            }
            return Json($"{{\"items\":[],\"cursor\":0,\"epoch\":\"{rotatedEpoch:D}\"}}");
        })) { BaseAddress = new("https://synthetic.invalid/") };
        try
        {
            await epochStore.SynchronizeLifeAsync(rotatedServer);
            throw new Exception("Expected a sync epoch change.");
        }
        catch (SyncEpochChangedException) { }
        if (epochStore.SyncStatus.EpochConflicts != 1 || epochStore.All<PlanItem>().Count != 1)
            throw new Exception("Epoch diagnostics must be recorded without discarding local data.");
        var learningPath = Path.Combine(Path.GetTempPath(), "personalflow-learning-" + Guid.NewGuid(), "client.db");
        var learningStore = new LocalStore(learningPath);
        var lessonId = Guid.NewGuid();
        learningStore.SaveLearningProgress(new LearningProgress(lessonId, 1, Answer: "Saved answer", Status: "InProgress"));
        var learningEpoch = Guid.NewGuid();
        var learningCalls = 0;
        string? learningWire = null;
        string? learningBody = null;
        using var learningClient = new HttpClient(new Handler(async request =>
        {
            if (request.Method == HttpMethod.Get && request.RequestUri!.AbsolutePath.EndsWith("/sync/state", StringComparison.Ordinal))
                return Json($"{{\"epoch\":\"{learningEpoch:D}\"}}");
            if (request.Method == HttpMethod.Post)
            {
                var raw = await request.Content!.ReadAsStringAsync();
                if (learningCalls++ == 0)
                {
                    learningWire = raw;
                    throw new HttpRequestException("Simulated Learning response loss");
                }
                if (learningWire != raw) throw new Exception("Learning retry changed the frozen mutation.");
                var wire = JsonNode.Parse(raw)!;
                learningBody = wire["patch"]!.ToJsonString();
                return Json($"{{\"status\":\"applied\",\"revision\":1,\"body\":{learningBody},\"deleted\":false}}");
            }
            var pageBody = JsonSerializer.Serialize(learningBody);
            return Json($"{{\"items\":[{{\"revision\":1,\"lessonId\":\"{lessonId:D}\",\"contentVersion\":1,\"body\":{pageBody},\"deleted\":false}}],\"cursor\":1,\"epoch\":\"{learningEpoch:D}\"}}");
        })) { BaseAddress = new("https://synthetic.invalid/") };
        try
        {
            await learningStore.SynchronizeLearningAsync(learningClient);
            throw new Exception("Expected a lost Learning response.");
        }
        catch (HttpRequestException) { }
        if (learningStore.Pending != 1) throw new Exception("A lost Learning response discarded the queued answer.");
        learningStore = new LocalStore(learningPath);
        await learningStore.SynchronizeLearningAsync(learningClient);
        var syncedProgress = learningStore.All<LearningProgress>().Single(x => x.LessonId == lessonId);
        if (learningStore.Pending != 0 || syncedProgress.Answer != "Saved answer")
            throw new Exception("Learning answer did not survive retry and change-feed reconciliation.");
        var conflictedLesson = Guid.NewGuid();
        learningStore.SaveLearningProgress(new LearningProgress(conflictedLesson, 1, Answer: "Keep this draft"));
        using var learningConflictClient = new HttpClient(new Handler(async request =>
        {
            if (request.Method == HttpMethod.Get && request.RequestUri!.AbsolutePath.EndsWith("/sync/state", StringComparison.Ordinal))
                return Json($"{{\"epoch\":\"{learningEpoch:D}\"}}");
            if (request.Method == HttpMethod.Post)
                return Json("{\"status\":\"conflict\",\"conflict\":{\"id\":\"synthetic-learning\"}}");
            return Json($"{{\"items\":[],\"cursor\":1,\"epoch\":\"{learningEpoch:D}\"}}");
        })) { BaseAddress = new("https://synthetic.invalid/") };
        await learningStore.SynchronizeLearningAsync(learningConflictClient);
        if (learningStore.Pending != 1 || learningStore.LearningSyncConflicts().Count != 1 ||
            learningStore.All<LearningProgress>().Single(x => x.LessonId == conflictedLesson).Answer != "Keep this draft")
            throw new Exception("A Learning conflict did not preserve its answer and remain visible.");
        var learningEpochPath = Path.Combine(Path.GetTempPath(), "personalflow-learning-epoch-" + Guid.NewGuid(), "client.db");
        var learningEpochStore = new LocalStore(learningEpochPath);
        var epochLessonId = Guid.NewGuid();
        learningEpochStore.SaveLearningProgress(new LearningProgress(epochLessonId, 1, Answer: "Retain after epoch"));
        var changedLearningEpoch = Guid.NewGuid();
        using var changedEpochServer = new HttpClient(new Handler(async request =>
        {
            if (request.Method == HttpMethod.Get && request.RequestUri!.AbsolutePath.EndsWith("/sync/state", StringComparison.Ordinal))
                return Json($"{{\"epoch\":\"{learningEpoch:D}\"}}");
            if (request.Method == HttpMethod.Post)
            {
                var wire = JsonNode.Parse(await request.Content!.ReadAsStringAsync())!;
                return Json($"{{\"status\":\"applied\",\"revision\":1,\"body\":{wire["patch"]!.ToJsonString()},\"deleted\":false}}");
            }
            return Json($"{{\"items\":[],\"cursor\":0,\"epoch\":\"{changedLearningEpoch:D}\"}}");
        })) { BaseAddress = new("https://synthetic.invalid/") };
        try
        {
            await learningEpochStore.SynchronizeLearningAsync(changedEpochServer);
            throw new Exception("Expected a Learning sync epoch change.");
        }
        catch (SyncEpochChangedException) { }
        if (learningEpochStore.SyncStatus.EpochConflicts != 1 ||
            learningEpochStore.All<LearningProgress>().Single().Answer != "Retain after epoch")
            throw new Exception("A Learning epoch change must retain local progress and be recorded.");
        Console.WriteLine("8 client sync checks passed (Life and Learning retry, conflict and epoch handling).");
    }
    static HttpResponseMessage Json(string value) => new(HttpStatusCode.OK) { Content = new StringContent(value, Encoding.UTF8, "application/json") };
    sealed class Handler(Func<HttpRequestMessage, Task<HttpResponseMessage>> send) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => send(request);
    }
}
