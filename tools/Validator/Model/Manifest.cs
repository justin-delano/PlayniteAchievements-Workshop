using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;

namespace Workshop.Validator.Model;

/// <summary>An item's manifest.json. Field order here is the order written to disk.</summary>
public sealed class Manifest
{
    [JsonPropertyName("schemaVersion")] public int SchemaVersion { get; set; } = ManifestSchema.Current;
    [JsonPropertyName("id")] public string Id { get; set; } = "";
    [JsonPropertyName("kind")] public ItemKind Kind { get; set; }
    [JsonPropertyName("name")] public string Name { get; set; } = "";
    [JsonPropertyName("description")] public string Description { get; set; } = "";
    /// <summary>Display name chosen by the submitter.</summary>
    [JsonPropertyName("author")] public string Author { get; set; } = "";
    /// <summary>GitHub login, when the item was submitted through the issue form by a GitHub user.</summary>
    [JsonPropertyName("authorGitHub")] public string? AuthorGitHub { get; set; }
    /// <summary>SHA-256 of the submitter key the extension holds, when submitted from Playnite; proves ownership on updates.</summary>
    [JsonPropertyName("ownerHash")] public string? OwnerHash { get; set; }
    /// <summary>GitHub logins allowed to update the item besides the author.</summary>
    [JsonPropertyName("maintainers")] public List<string> Maintainers { get; set; } = new();
    [JsonPropertyName("version")] public string Version { get; set; } = "1.0.0";
    [JsonPropertyName("license")] public string License { get; set; } = "CC-BY-4.0";
    [JsonPropertyName("tags")] public List<string> Tags { get; set; } = new();
    [JsonPropertyName("minPluginVersion")] public string MinPluginVersion { get; set; } = "";
    [JsonPropertyName("created")] public string Created { get; set; } = "";
    [JsonPropertyName("updated")] public string Updated { get; set; } = "";
    [JsonPropertyName("game")] public GameInfo? Game { get; set; }
    [JsonPropertyName("contents")] public JsonObject Contents { get; set; } = new();
    [JsonPropertyName("package")] public PackageInfo Package { get; set; } = new();
    /// <summary>Image the extension draws from the package.</summary>
    [JsonPropertyName("preview")] public string? Preview { get; set; }
    /// <summary>Optional image the sharer chose for listings, such as game banner art.</summary>
    [JsonPropertyName("cover")] public string? Cover { get; set; }
    [JsonPropertyName("readme")] public string Readme { get; set; } = "README.md";

    public static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Converters = { new JsonStringEnumConverter() },
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };

    public static Manifest Load(string path)
    {
        var text = File.ReadAllText(path);
        return JsonSerializer.Deserialize<Manifest>(text, JsonOptions)
               ?? throw new Cli.ValidationException($"{path}: not a manifest.");
    }

    public void Save(string path)
    {
        File.WriteAllText(path, JsonSerializer.Serialize(this, JsonOptions) + "\n");
    }

    /// <summary>
    /// Whether a submitter may change this item: the GitHub login that created it or a listed
    /// maintainer, or the holder of the submitter key the extension used to create it.
    /// </summary>
    public bool IsOwnedBy(string? gitHubLogin, string? submitterHash)
    {
        if (!string.IsNullOrWhiteSpace(gitHubLogin) &&
            (string.Equals(AuthorGitHub, gitHubLogin, StringComparison.OrdinalIgnoreCase) ||
             Maintainers.Any(m => string.Equals(m, gitHubLogin, StringComparison.OrdinalIgnoreCase))))
        {
            return true;
        }

        return !string.IsNullOrWhiteSpace(submitterHash) &&
               !string.IsNullOrWhiteSpace(OwnerHash) &&
               string.Equals(OwnerHash, submitterHash, StringComparison.OrdinalIgnoreCase);
    }
}

public sealed class GameInfo
{
    [JsonPropertyName("name")] public string Name { get; set; } = "";
    [JsonPropertyName("platform")] public string? Platform { get; set; }
    [JsonPropertyName("keys")] public List<GameKey> Keys { get; set; } = new();
}

/// <summary>Mirrors the extension's PortableGameKey.</summary>
public sealed class GameKey
{
    [JsonPropertyName("providerKey")] public string? ProviderKey { get; set; }
    [JsonPropertyName("providerPlatformKey")] public string? ProviderPlatformKey { get; set; }
    [JsonPropertyName("providerGameId")] public int? ProviderGameId { get; set; }
    [JsonPropertyName("providerGameKey")] public string? ProviderGameKey { get; set; }
    [JsonPropertyName("name")] public string? Name { get; set; }
    [JsonPropertyName("platform")] public string? Platform { get; set; }

    /// <summary>The folder segment a game lives under: provider plus id, or name-based.</summary>
    public string FolderKey()
    {
        if (!string.IsNullOrWhiteSpace(ProviderKey))
        {
            var id = ProviderGameId is > 0 ? ProviderGameId.ToString() : ProviderGameKey;
            if (!string.IsNullOrWhiteSpace(id))
            {
                return Slug.From(ProviderKey + "-" + id);
            }
        }

        return "name-" + Slug.From(Name ?? "unknown");
    }
}

public sealed class PackageInfo
{
    [JsonPropertyName("file")] public string File { get; set; } = "";
    [JsonPropertyName("formatKind")] public string FormatKind { get; set; } = "";
    [JsonPropertyName("formatVersion")] public int FormatVersion { get; set; }
    [JsonPropertyName("sizeBytes")] public long SizeBytes { get; set; }
    [JsonPropertyName("sha256")] public string Sha256 { get; set; } = "";
    [JsonPropertyName("release")] public ReleaseInfo Release { get; set; } = new();
}

public sealed class ReleaseInfo
{
    [JsonPropertyName("tag")] public string Tag { get; set; } = "";
    [JsonPropertyName("url")] public string Url { get; set; } = "";
}

public static class ManifestSchema
{
    public const int Current = 1;
}

public static class Slug
{
    /// <summary>Lowercase kebab-case from any text; empty input becomes "item".</summary>
    public static string From(string text)
    {
        var chars = text.Trim().ToLowerInvariant()
            .Select(c => char.IsLetterOrDigit(c) && c < 128 ? c : '-')
            .ToArray();
        var collapsed = string.Join("-", new string(chars).Split('-', StringSplitOptions.RemoveEmptyEntries));
        if (collapsed.Length > 60)
        {
            collapsed = collapsed[..60].TrimEnd('-');
        }

        return collapsed.Length == 0 ? "item" : collapsed;
    }
}
