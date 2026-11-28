using Microsoft.Data.Sqlite;
using System.Globalization;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace PersonalFlow.Client;

public sealed partial class LocalStore
{
    sealed record LearningLessonEnvelope(Guid Id, int Version, DateOnly Date, JsonElement Content);

    public List<CachedLesson> GetCachedLessons(DateOnly? from = null, DateOnly? to = null)
    {
        if (from is { } start && to is { } end && start > end)
            throw new ArgumentException("The start date must not follow the end date.");

        using var db = Open();
        using var c = db.CreateCommand();
        c.CommandText = """
            SELECT lesson_id,content_version,lesson_date,content FROM learning_lessons
            WHERE ($from IS NULL OR lesson_date >= $from) AND ($to IS NULL OR lesson_date <= $to)
            ORDER BY lesson_date,lesson_id,content_version
            """;
        c.Parameters.AddWithValue("$from", (object?)from?.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) ?? DBNull.Value);
        c.Parameters.AddWithValue("$to", (object?)to?.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) ?? DBNull.Value);
        using var reader = c.ExecuteReader();
        var lessons = new List<CachedLesson>();
        while (reader.Read())
        {
            var date = DateOnly.ParseExact(reader.GetString(2), "yyyy-MM-dd", CultureInfo.InvariantCulture);
            lessons.Add(new(Guid.Parse(reader.GetString(0)), reader.GetInt32(1), date, reader.GetString(3)));
        }
        return lessons;
    }

    public CachedLesson? GetCachedLesson(Guid lessonId, int version) =>
        GetCachedLessons().FirstOrDefault(lesson => lesson.Id == lessonId && lesson.Version == version);

    public LearningLessonContent? GetCachedLessonContent(Guid lessonId, int version)
    {
        var cached = GetCachedLesson(lessonId, version);
        if (cached is null) return null;
        var content = JsonSerializer.Deserialize<LearningLessonContent>(cached.Content, LearningWire)
            ?? throw new InvalidDataException("Cached lesson content is empty.");
        if (content.Id != cached.Id || content.Version != cached.Version || content.Date != cached.Date ||
            content.Sources is null || content.Sources.Any(source => source is null))
            throw new InvalidDataException("Cached lesson metadata does not match its index.");
        return content;
    }

    // The caller supplies an authenticated client. Lesson versions are immutable after they enter SQLite.
    public async Task<int> DownloadLessonsAsync(
        HttpClient client,
        DateOnly? from = null,
        DateOnly? to = null,
        CancellationToken cancellationToken = default)
    {
        if (client.BaseAddress is null || client.BaseAddress.Scheme != Uri.UriSchemeHttps)
            throw new InvalidOperationException("HTTPS API address required.");
        if (from is { } start && to is { } end && start > end)
            throw new ArgumentException("The start date must not follow the end date.");

        var query = new List<string>();
        if (from is { } first) query.Add($"from={first.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)}");
        if (to is { } last) query.Add($"to={last.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)}");
        var path = "v1/lessons" + (query.Count == 0 ? "" : "?" + string.Join("&", query));
        var received = await client.GetFromJsonAsync<List<LearningLessonEnvelope>>(path, LearningWire, cancellationToken)
            ?? throw new InvalidDataException("Empty Learning lesson response.");
        if (received.Count > 1000)
            throw new InvalidDataException("The lesson range is too large; download a smaller date range.");

        var prepared = new List<CachedLesson>(received.Count);
        var keys = new HashSet<(Guid Id, int Version)>();
        foreach (var item in received)
        {
            if (item.Id == Guid.Empty || item.Version < 1 || item.Content.ValueKind != JsonValueKind.Object ||
                !keys.Add((item.Id, item.Version)))
                throw new InvalidDataException("The lesson response contains invalid or duplicate versions.");

            var content = item.Content;
            if (!content.TryGetProperty("id", out var contentId) || !contentId.TryGetGuid(out var id) || id != item.Id ||
                !content.TryGetProperty("version", out var contentVersion) || !contentVersion.TryGetInt32(out var version) || version != item.Version ||
                !content.TryGetProperty("date", out var contentDate) ||
                !DateOnly.TryParseExact(contentDate.GetString(), "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var date) ||
                date != item.Date)
                throw new InvalidDataException("Lesson metadata does not match its immutable content.");

            var body = content.GetRawText();
            if (body.Length > 250_000)
                throw new InvalidDataException("A lesson exceeds the supported offline cache size.");
            prepared.Add(new(item.Id, item.Version, item.Date, body));
        }

        using var db = Open();
        using var tx = db.BeginTransaction();
        using var c = db.CreateCommand();
        c.Transaction = tx;
        var added = 0;
        foreach (var lesson in prepared)
        {
            c.Parameters.Clear();
            c.CommandText = "SELECT lesson_date,content FROM learning_lessons WHERE lesson_id=$id AND content_version=$version";
            c.Parameters.AddWithValue("$id", lesson.Id.ToString());
            c.Parameters.AddWithValue("$version", lesson.Version);
            using (var reader = c.ExecuteReader())
            {
                if (reader.Read())
                {
                    var oldDate = reader.GetString(0);
                    var oldContent = reader.GetString(1);
                    reader.Close();
                    if (oldDate != lesson.Date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) ||
                        !JsonNode.DeepEquals(JsonNode.Parse(oldContent), JsonNode.Parse(lesson.Content)))
                        throw new InvalidDataException("A published lesson version changed after it was cached.");
                    continue;
                }
            }

            c.Parameters.Clear();
            c.CommandText = "INSERT INTO learning_lessons(lesson_id,content_version,lesson_date,content) VALUES($id,$version,$date,$content)";
            c.Parameters.AddWithValue("$id", lesson.Id.ToString());
            c.Parameters.AddWithValue("$version", lesson.Version);
            c.Parameters.AddWithValue("$date", lesson.Date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture));
            c.Parameters.AddWithValue("$content", lesson.Content);
            c.ExecuteNonQuery();
            added++;
        }
        tx.Commit();
        return added;
    }
}
