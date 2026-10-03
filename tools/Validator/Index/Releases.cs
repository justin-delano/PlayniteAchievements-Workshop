using System.Text.Json;
using System.Text.Json.Nodes;

namespace Workshop.Validator.Index;

public sealed record ReleaseAsset(string Name, long Size, long DownloadCount, string BrowserDownloadUrl);

/// <summary>
/// The repository's releases as `gh api repos/:o/:r/releases --paginate` prints them: one JSON
/// array per page, concatenated. Looked up by tag; each release holds one asset per item version.
/// </summary>
public sealed class Releases
{
    private readonly Dictionary<string, List<ReleaseAsset>> _byTag = new(StringComparer.Ordinal);

    public static Releases Load(string? path)
    {
        var releases = new Releases();
        if (path is null || !File.Exists(path))
        {
            return releases;
        }

        var text = File.ReadAllText(path).Trim();
        if (text.Length == 0)
        {
            return releases;
        }

        // --paginate writes "[...][...]": one array per page with nothing between. Read one
        // top-level value at a time and advance by the bytes it consumed.
        var bytes = System.Text.Encoding.UTF8.GetBytes(text);
        var offset = 0;
        while (offset < bytes.Length)
        {
            var span = new ReadOnlySpan<byte>(bytes, offset, bytes.Length - offset);
            var reader = new Utf8JsonReader(span, isFinalBlock: true, state: default);
            if (!reader.Read())
            {
                break;
            }

            if (reader.TokenType != JsonTokenType.StartArray)
            {
                reader.Skip();
                offset += (int)reader.BytesConsumed;
                continue;
            }

            var page = JsonNode.Parse(ref reader) as JsonArray;
            offset += (int)reader.BytesConsumed;
            if (page is null)
            {
                continue;
            }

            foreach (var release in page.OfType<JsonObject>())
            {
                var tag = release["tag_name"]?.GetValue<string>();
                if (string.IsNullOrEmpty(tag))
                {
                    continue;
                }

                var assets = new List<ReleaseAsset>();
                foreach (var asset in (release["assets"] as JsonArray ?? new JsonArray()).OfType<JsonObject>())
                {
                    assets.Add(new ReleaseAsset(
                        asset["name"]?.GetValue<string>() ?? "",
                        asset["size"]?.GetValue<long>() ?? 0,
                        asset["download_count"]?.GetValue<long>() ?? 0,
                        asset["browser_download_url"]?.GetValue<string>() ?? ""));
                }

                _ = releases._byTag.TryAdd(tag, assets);
            }
        }

        return releases;
    }

    public bool HasAny => _byTag.Count > 0;

    public IReadOnlyList<ReleaseAsset> AssetsFor(string tag) =>
        _byTag.TryGetValue(tag, out var assets) ? assets : Array.Empty<ReleaseAsset>();

    public ReleaseAsset? Asset(string tag, string fileName) =>
        AssetsFor(tag).FirstOrDefault(asset => string.Equals(asset.Name, fileName, StringComparison.OrdinalIgnoreCase));

    public long TotalDownloads(string tag) => AssetsFor(tag).Sum(asset => asset.DownloadCount);
}
