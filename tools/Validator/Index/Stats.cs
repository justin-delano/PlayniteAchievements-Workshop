using System.Text.Json.Nodes;

namespace Workshop.Validator.Index;

/// <summary>
/// The Workshop service's statistics as `GET /v1/stats` answers them: install and like counts per
/// item id. They are the Workshop's own numbers rather than GitHub's release download counts,
/// which also counted previews and retries. The listings fall back to zeroes when the file is
/// missing or unreadable, so a service outage never fails an index build.
/// </summary>
public sealed class Stats
{
    private readonly Dictionary<string, long> _installs = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, long> _likes = new(StringComparer.OrdinalIgnoreCase);

    public static Stats Load(string? path)
    {
        var stats = new Stats();
        if (path is null || !File.Exists(path))
        {
            return stats;
        }

        try
        {
            var root = JsonNode.Parse(File.ReadAllText(path)) as JsonObject;
            Read(root?["installs"] as JsonObject, stats._installs);
            Read(root?["likes"] as JsonObject, stats._likes);
        }
        catch (Exception ex) when (ex is System.Text.Json.JsonException || ex is IOException)
        {
        }

        return stats;
    }

    private static void Read(JsonObject? counts, Dictionary<string, long> into)
    {
        foreach (var pair in counts ?? new JsonObject())
        {
            if (pair.Value?.GetValue<long>() is long count && count > 0)
            {
                into[pair.Key] = count;
            }
        }
    }

    public long Installs(string itemId) => _installs.TryGetValue(itemId, out var count) ? count : 0;

    public long Likes(string itemId) => _likes.TryGetValue(itemId, out var count) ? count : 0;
}
