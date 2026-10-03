using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Nodes;
using Workshop.Validator.Checks;
using Workshop.Validator.Cli;
using Workshop.Validator.Formats;
using Workshop.Validator.Index;
using Workshop.Validator.Intake;
using Workshop.Validator.Model;

namespace Workshop.Validator.Commands;

/// <summary>
/// Checks item folders: manifest rules, README and preview, and that the release asset the
/// manifest points at exists, hashes as declared, and yields the declared contents. On a pull
/// request it also checks that whoever changed an existing item may do so.
/// </summary>
public static class ValidateCommand
{
    public static async Task<int> RunAsync(Args args)
    {
        var root = Path.GetFullPath(args.Get("root") ?? ".");
        var releases = Releases.Load(args.Get("releases"));
        var baseRef = args.Get("base-ref");
        var actor = args.Get("actor");
        var repoOwner = args.Get("repo")?.Split('/')[0];
        var download = !args.Has("no-download");

        IReadOnlyList<ItemFolder> items;
        if (args.Get("changed") is { } changedFile)
        {
            items = File.ReadAllLines(changedFile)
                .Select(line => line.Trim())
                .Where(line => line.Length > 0)
                .Select(line => ItemLocator.FromRelativePath(root, line))
                .Where(item => item is not null)
                .Select(item => item!)
                .Distinct()
                .ToList();
        }
        else
        {
            items = ItemLocator.All(root);
        }

        var failures = 0;
        foreach (var item in items)
        {
            var errors = new List<string>();
            var manifestPath = Path.Combine(item.Path, "manifest.json");
            if (!Directory.Exists(item.Path))
            {
                // Deleted in this PR: a removal. Nothing to validate beyond ownership.
                if (baseRef is not null && actor is not null)
                {
                    CheckOwnership(root, item, baseRef, actor, repoOwner, null, errors);
                }
            }
            else if (!File.Exists(manifestPath))
            {
                errors.Add("manifest.json is missing.");
            }
            else
            {
                Manifest? manifest = null;
                try
                {
                    manifest = Manifest.Load(manifestPath);
                }
                catch (JsonException ex)
                {
                    errors.Add($"manifest.json is not valid: {ex.Message}");
                }

                if (manifest is not null)
                {
                    errors.AddRange(ManifestChecks.Run(manifest, item));
                    CheckFiles(item, manifest, errors);
                    if (baseRef is not null && actor is not null)
                    {
                        CheckOwnership(root, item, baseRef, actor, repoOwner, manifest, errors);
                    }

                    if (releases.HasAny)
                    {
                        await CheckReleaseAsync(manifest, releases, download, errors);
                    }
                }
            }

            if (errors.Count == 0)
            {
                Console.WriteLine($"ok    {item.Id}");
                continue;
            }

            failures++;
            Console.WriteLine($"FAIL  {item.Id}");
            foreach (var error in errors)
            {
                Console.WriteLine($"      - {error}");
            }
        }

        Console.WriteLine($"{items.Count} item(s) checked, {failures} failed.");
        return failures == 0 ? 0 : 1;
    }

    private static void CheckFiles(ItemFolder item, Manifest manifest, List<string> errors)
    {
        var readme = Path.Combine(item.Path, "README.md");
        if (!File.Exists(readme) || File.ReadAllText(readme).Trim().Length == 0)
        {
            errors.Add("README.md is missing or empty.");
        }

        if (manifest.Preview is not null)
        {
            var preview = Path.Combine(item.Path, manifest.Preview);
            if (!File.Exists(preview))
            {
                errors.Add($"preview '{manifest.Preview}' is missing.");
            }
            else if (!ZipGuard.IsImage(ZipGuard.ReadHead(preview)))
            {
                errors.Add($"preview '{manifest.Preview}' is not a PNG, JPEG, GIF or WebP image.");
            }
            else if (new FileInfo(preview).Length > ZipGuard.MaxPreviewBytes)
            {
                errors.Add($"preview '{manifest.Preview}' is larger than {ZipGuard.MaxPreviewBytes / (1024 * 1024)} MB.");
            }
        }

        var allowed = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "manifest.json", "README.md" };
        if (manifest.Preview is not null)
        {
            allowed.Add(manifest.Preview);
        }

        foreach (var file in Directory.GetFiles(item.Path))
        {
            if (!allowed.Contains(Path.GetFileName(file)))
            {
                errors.Add($"Unexpected file '{Path.GetFileName(file)}'. An item folder holds only manifest.json, README.md and the preview; the package lives on the release.");
            }
        }

        if (Directory.GetDirectories(item.Path).Length > 0)
        {
            errors.Add("An item folder has no subfolders.");
        }
    }

    /// <summary>
    /// An existing item may be changed by its GitHub author, a maintainer, or the repository
    /// owner. Bots (the intake workflow) already enforced ownership from the issue. The
    /// identity fields themselves may not be rewritten by anyone but the owner.
    /// </summary>
    private static void CheckOwnership(string root, ItemFolder item, string baseRef, string actor, string? repoOwner, Manifest? updated, List<string> errors)
    {
        if (actor.EndsWith("[bot]", StringComparison.Ordinal) ||
            (repoOwner is not null && string.Equals(actor, repoOwner, StringComparison.OrdinalIgnoreCase)))
        {
            return;
        }

        var previousJson = GitShow(root, $"{baseRef}:{item.Id}/manifest.json");
        if (previousJson is null)
        {
            return;
        }

        Manifest previous;
        try
        {
            previous = JsonSerializer.Deserialize<Manifest>(previousJson, Manifest.JsonOptions)!;
        }
        catch (JsonException)
        {
            return;
        }

        if (!previous.IsOwnedBy(actor, null))
        {
            errors.Add($"@{actor} is not the author or a maintainer of this item, so the change needs the owner or a repository maintainer.");
        }

        if (updated is not null &&
            (updated.AuthorGitHub != previous.AuthorGitHub || updated.OwnerHash != previous.OwnerHash) &&
            !previous.IsOwnedBy(actor, null))
        {
            errors.Add("authorGitHub and ownerHash can only be changed by the item's owner.");
        }
    }

    private static async Task CheckReleaseAsync(Manifest manifest, Releases releases, bool download, List<string> errors)
    {
        var asset = releases.Asset(manifest.Package.Release.Tag, manifest.Package.File);
        if (asset is null)
        {
            errors.Add($"Release '{manifest.Package.Release.Tag}' has no asset named '{manifest.Package.File}'.");
            return;
        }

        if (!string.Equals(asset.BrowserDownloadUrl, manifest.Package.Release.Url, StringComparison.Ordinal))
        {
            errors.Add($"package.release.url does not match the asset's download URL ({asset.BrowserDownloadUrl}).");
        }

        if (asset.Size != manifest.Package.SizeBytes)
        {
            errors.Add($"package.sizeBytes is {manifest.Package.SizeBytes} but the asset is {asset.Size} bytes.");
        }

        if (!download)
        {
            return;
        }

        var temp = Path.Combine(Path.GetTempPath(), "workshop-validator", Guid.NewGuid().ToString("N"), manifest.Package.File);
        try
        {
            using var downloader = new Downloader(Environment.GetEnvironmentVariable("GH_TOKEN") ?? Environment.GetEnvironmentVariable("GITHUB_TOKEN"));
            await downloader.DownloadAsync(asset.BrowserDownloadUrl, temp);

            var sha = ZipGuard.Sha256Hex(temp);
            if (!string.Equals(sha, manifest.Package.Sha256, StringComparison.OrdinalIgnoreCase))
            {
                errors.Add($"package.sha256 is {manifest.Package.Sha256} but the asset hashes to {sha}.");
            }

            var report = PackageInspector.Inspect(temp, manifest.Kind);
            errors.AddRange(report.Errors.Select(error => "asset: " + error));
            if (report.Ok)
            {
                if (report.FormatKind != manifest.Package.FormatKind || report.FormatVersion != manifest.Package.FormatVersion)
                {
                    errors.Add($"package.formatKind/formatVersion ({manifest.Package.FormatKind} v{manifest.Package.FormatVersion}) do not match the asset ({report.FormatKind} v{report.FormatVersion}).");
                }

                if (!JsonNode.DeepEquals(report.Contents, manifest.Contents))
                {
                    errors.Add("contents do not match what the asset contains. Run `inspect` on the asset and copy its contents into the manifest.");
                }

                if (manifest.Kind == ItemKind.GameCustomData && report.Game is not null && manifest.Game is not null &&
                    report.Game.Keys[0].FolderKey() != manifest.Game.Keys[0].FolderKey())
                {
                    errors.Add("game keys do not match the asset's game keys.");
                }
            }
        }
        finally
        {
            try { Directory.Delete(Path.GetDirectoryName(temp)!, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        }
    }

    private static string? GitShow(string root, string spec)
    {
        try
        {
            var psi = new ProcessStartInfo("git", $"show \"{spec}\"")
            {
                WorkingDirectory = root,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false
            };
            using var process = Process.Start(psi)!;
            var output = process.StandardOutput.ReadToEnd();
            process.WaitForExit();
            return process.ExitCode == 0 ? output : null;
        }
        catch (Exception)
        {
            return null;
        }
    }
}
