using System.Text.Json;
using System.Text.Json.Nodes;
using Workshop.Validator.Cli;
using Workshop.Validator.Index;
using Workshop.Validator.Model;

namespace Workshop.Validator.Commands;

/// <summary>
/// Writes index/v1.json (and latest.json) from every manifest, with download counts from the
/// releases and URLs the extension can fetch, then regenerates the per-type and per-game
/// README listings.
/// </summary>
public static class BuildIndexCommand
{
    public const int IndexSchemaVersion = 1;

    public static int Run(Args args)
    {
        var root = Path.GetFullPath(args.Get("root") ?? ".");
        var repo = args.Require("repo");
        var commit = args.Get("commit") ?? "0000000000000000000000000000000000000000";
        var releases = Releases.Load(args.Get("releases"));

        var entries = new List<(Manifest Manifest, long Total, long Current)>();
        var items = new JsonArray();
        foreach (var item in ItemLocator.All(root))
        {
            var manifest = Manifest.Load(Path.Combine(item.Path, "manifest.json"));
            var total = releases.TotalDownloads(manifest.Package.Release.Tag);
            var current = releases.Asset(manifest.Package.Release.Tag, manifest.Package.File)?.DownloadCount ?? 0;
            entries.Add((manifest, total, current));

            var node = JsonSerializer.SerializeToNode(manifest, Manifest.JsonOptions)!.AsObject();
            node["downloads"] = new JsonObject { ["total"] = total, ["current"] = current };
            node["urls"] = new JsonObject
            {
                ["package"] = manifest.Package.Release.Url,
                ["preview"] = manifest.Preview is null ? null : Cdn(repo, commit, $"{manifest.Id}/{manifest.Preview}"),
                ["cover"] = manifest.Cover is null ? null : Cdn(repo, commit, $"{manifest.Id}/{manifest.Cover}"),
                ["readme"] = Cdn(repo, commit, $"{manifest.Id}/README.md"),
                ["folder"] = $"https://github.com/{repo}/tree/main/{manifest.Id}"
            };
            items.Add(node);
        }

        var index = new JsonObject
        {
            ["schemaVersion"] = IndexSchemaVersion,
            ["generated"] = DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ssZ"),
            ["commit"] = commit,
            ["repository"] = repo,
            ["items"] = items
        };

        var indexDir = Path.Combine(root, "index");
        Directory.CreateDirectory(indexDir);
        var json = index.ToJsonString(Manifest.JsonOptions) + "\n";
        File.WriteAllText(Path.Combine(indexDir, $"v{IndexSchemaVersion}.json"), json);
        File.WriteAllText(Path.Combine(indexDir, "latest.json"), json);

        ReadmeGenerator.WriteAll(root, repo, entries);
        Console.WriteLine($"{entries.Count} item(s) indexed.");
        return 0;
    }

    /// <summary>jsDelivr URL pinned to the commit, so the CDN's copy never goes stale.</summary>
    private static string Cdn(string repo, string commit, string path) =>
        $"https://cdn.jsdelivr.net/gh/{repo}@{commit}/{path}";
}
