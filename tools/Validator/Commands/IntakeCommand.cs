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
/// Turns a submission issue into a validated item folder and a package ready for upload.
/// Writes <c>work/result.json</c> for the workflow in every case: errors go back to the issue
/// as a comment, success continues to the release upload and the pull request.
/// </summary>
public static class IntakeCommand
{
    // Field labels exactly as .github/ISSUE_TEMPLATE/submit.yml declares them.
    private const string KindField = "Kind";
    private const string NameField = "Name";
    private const string AuthorField = "Author name";
    private const string DescriptionField = "Description";
    private const string TagsField = "Tags";
    private const string LicenseField = "License";
    private const string ExistingIdField = "Existing item id (updates only)";
    private const string RequestField = "Request";
    private const string ReadmeField = "README";
    private const string FilesField = "Files";
    private const string PackageUrlField = "Package URL (for packages over 25 MB)";
    private const string SubmitterHashField = "Submitter key (filled in by the extension)";

    public static async Task<int> RunAsync(Args args)
    {
        var issuePath = args.Require("issue");
        var work = args.Require("work");
        var root = args.Get("root") ?? ".";
        var actor = args.Get("actor") ?? "";
        var repo = args.Require("repo");

        var result = new JsonObject
        {
            ["ok"] = false,
            ["remove"] = false,
            ["isUpdate"] = false,
            ["errors"] = new JsonArray()
        };

        try
        {
            await ProcessAsync(issuePath, work, root, actor, repo, result);
        }
        catch (ValidationException ex)
        {
            ((JsonArray)result["errors"]!).Add(ex.Message);
        }

        result["ok"] = ((JsonArray)result["errors"]!).Count == 0;
        Directory.CreateDirectory(work);
        File.WriteAllText(Path.Combine(work, "result.json"), result.ToJsonString(Manifest.JsonOptions) + "\n");
        Console.WriteLine(result.ToJsonString(Manifest.JsonOptions));
        return 0;
    }

    private static async Task ProcessAsync(string issuePath, string work, string root, string actor, string repo, JsonObject result)
    {
        var errors = (JsonArray)result["errors"]!;
        var issue = JsonNode.Parse(File.ReadAllText(issuePath)) as JsonObject
                    ?? throw new ValidationException("The issue payload could not be read.");
        var form = IssueForm.Parse(issue["body"]?.GetValue<string>() ?? "");
        var login = issue["user"]?["login"]?.GetValue<string>() ?? actor;

        var submitterHash = form.Get(SubmitterHashField).Trim().ToLowerInvariant();
        if (submitterHash.Length > 0 && !ManifestChecks.Sha256Pattern().IsMatch(submitterHash))
        {
            errors.Add("The submitter key is not a SHA-256 hex string. Leave it empty when submitting by hand.");
            submitterHash = "";
        }

        var remove = form.IsChecked(RequestField, "Remove this item");
        var existingId = form.Get(ExistingIdField).Trim().Trim('`').Trim('/');
        Manifest? existing = null;
        ItemFolder? existingFolder = null;
        if (existingId.Length > 0)
        {
            existingFolder = ItemLocator.FromRelativePath(root, existingId);
            var manifestPath = existingFolder is null ? null : Path.Combine(existingFolder.Path, "manifest.json");
            if (manifestPath is null || !File.Exists(manifestPath))
            {
                throw new ValidationException($"No item with id `{existingId}` exists. Leave the field empty for a new item.");
            }

            existing = Manifest.Load(manifestPath);
            if (!existing.IsOwnedBy(login, submitterHash))
            {
                throw new ValidationException($"`{existingId}` was submitted by someone else, so it can only be updated by its author or a listed maintainer.");
            }

            result["isUpdate"] = true;
        }
        else if (remove)
        {
            throw new ValidationException("To remove an item, fill in its id in the existing item field.");
        }

        if (remove)
        {
            result["remove"] = true;
            result["id"] = existing!.Id;
            result["tag"] = existing.Id;
            result["name"] = existing.Name;
            result["assetName"] = "";
            return;
        }

        // Fields.
        var kind = Kinds.FromFormLabel(form.Get(KindField));
        if (kind is null)
        {
            throw new ValidationException("Pick a kind.");
        }

        if (existing is not null && existing.Kind != kind)
        {
            throw new ValidationException($"`{existing.Id}` is a {Kinds.DisplayName(existing.Kind).ToLowerInvariant()} item; an update must be the same kind.");
        }

        var name = form.Get(NameField).Trim();
        var description = form.Get(DescriptionField).Trim();
        var author = form.Get(AuthorField).Trim();
        if (author.Length == 0)
        {
            author = login;
        }

        var license = form.Get(LicenseField).Trim();
        var tags = form.Get(TagsField)
            .Split(new[] { ',', '\n', ';' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(Slug.From)
            .Where(tag => tag != "item")
            .Distinct(StringComparer.Ordinal)
            .ToList();
        var readme = form.Get(ReadmeField).Trim();

        if (name.Length == 0 || name.Length > ManifestChecks.MaxNameLength)
        {
            errors.Add($"Name is required and at most {ManifestChecks.MaxNameLength} characters.");
        }

        if (description.Length == 0 || description.Length > ManifestChecks.MaxDescriptionLength)
        {
            errors.Add($"Description is required and at most {ManifestChecks.MaxDescriptionLength} characters.");
        }

        if (author.Length > ManifestChecks.MaxAuthorLength)
        {
            errors.Add($"Author name is at most {ManifestChecks.MaxAuthorLength} characters.");
        }

        if (!ManifestChecks.Licenses.Contains(license))
        {
            errors.Add("Pick a license.");
        }

        if (tags.Count > ManifestChecks.MaxTags)
        {
            errors.Add($"At most {ManifestChecks.MaxTags} tags.");
        }

        // Files: the first non-image attachment is the package, the first image is the preview;
        // a package URL replaces the attachment for large files.
        var attachments = form.Attachments(FilesField);
        var packageAttachment = attachments.FirstOrDefault(a => !a.IsImageLink && !LooksLikeImage(a.Name));
        var previewAttachment = attachments.FirstOrDefault(a => a.IsImageLink || LooksLikeImage(a.Name));
        var packageUrl = form.Get(PackageUrlField).Trim();
        var sourceUrl = packageAttachment?.Url ?? (packageUrl.Length > 0 ? packageUrl : null);
        if (sourceUrl is null)
        {
            errors.Add("Attach the package file or fill in the package URL.");
        }

        if (errors.Count > 0)
        {
            return;
        }

        var downloadDir = Path.Combine(work, "download");
        using var downloader = new Downloader(Environment.GetEnvironmentVariable("GH_TOKEN") ?? Environment.GetEnvironmentVariable("GITHUB_TOKEN"));

        var downloadedName = packageAttachment?.Name ?? Path.GetFileName(new Uri(sourceUrl!).AbsolutePath);
        var packagePath = Path.Combine(downloadDir, "package.bin");
        await downloader.DownloadAsync(sourceUrl!, packagePath);

        var report = PackageInspector.Inspect(packagePath, kind);
        foreach (var error in report.Errors)
        {
            errors.Add(error);
        }

        if (errors.Count > 0 || report.Kind is null)
        {
            return;
        }

        // Identity and version.
        var slug = existing is not null ? existing.Id.Split('/').Last() : Slug.From(name);
        string id;
        if (existing is not null)
        {
            id = existing.Id;
        }
        else if (report.Kind == ItemKind.GameCustomData)
        {
            id = $"{Kinds.Folder(report.Kind.Value)}/{report.Game!.Keys[0].FolderKey()}/{slug}";
        }
        else
        {
            id = $"{Kinds.Folder(report.Kind.Value)}/{slug}";
        }

        if (existing is null && Directory.Exists(Path.Combine(root, id.Replace('/', Path.DirectorySeparatorChar))))
        {
            throw new ValidationException($"An item with id `{id}` already exists. To update it, fill in the existing item id; otherwise choose a different name.");
        }

        var version = existing is null ? "1.0.0" : BumpPatch(existing.Version);
        var today = DateTime.UtcNow.ToString("yyyy-MM-dd");
        var extension = CanonicalExtension(report);
        var assetName = $"{slug}-{version}{extension}";

        // An issue the Worker opened on a Playnite user's behalf is authored by the bot account
        // and identifies the real submitter only by hash; a hand-filled form identifies them by
        // login. A hash therefore means "hash-owned", and the opening login is not recorded.
        var botLogin = Environment.GetEnvironmentVariable("WORKSHOP_BOT_LOGIN");
        var isBotIssue = submitterHash.Length > 0 ||
                         login.EndsWith("[bot]", StringComparison.Ordinal) ||
                         (botLogin is not null && string.Equals(login, botLogin, StringComparison.OrdinalIgnoreCase));
        if (isBotIssue && author == login)
        {
            errors.Add("Author name is required for submissions made from Playnite.");
        }

        var manifest = new Manifest
        {
            Id = id,
            Kind = report.Kind.Value,
            Name = name,
            Description = description,
            Author = author,
            AuthorGitHub = !isBotIssue && ManifestChecks.LoginPattern().IsMatch(login) ? login : existing?.AuthorGitHub,
            OwnerHash = submitterHash.Length > 0 ? submitterHash : existing?.OwnerHash,
            Maintainers = existing?.Maintainers ?? new List<string>(),
            Version = version,
            License = license,
            Tags = tags,
            MinPluginVersion = Kinds.MinPluginVersion(report.Kind.Value, report.FormatVersion),
            Created = existing?.Created ?? today,
            Updated = today,
            Game = report.Game,
            Contents = report.Contents,
            Package = new PackageInfo
            {
                File = assetName,
                FormatKind = report.FormatKind,
                FormatVersion = report.FormatVersion,
                SizeBytes = new FileInfo(packagePath).Length,
                Sha256 = ZipGuard.Sha256Hex(packagePath),
                Release = new ReleaseInfo { Tag = id, Url = "" }
            }
        };

        // The Worker-submitted bot issue carries the extension user's hash and no login; a
        // GitHub user submitting by hand carries a login and usually no hash.
        if (manifest.AuthorGitHub is null && manifest.OwnerHash is null)
        {
            throw new ValidationException("The submission has neither a GitHub author nor a submitter key, so nobody could update it later.");
        }

        var itemDir = Path.Combine(work, "item", id.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(itemDir);

        if (previewAttachment is not null)
        {
            var previewTemp = Path.Combine(downloadDir, "preview.bin");
            await downloader.DownloadAsync(previewAttachment.Url, previewTemp);
            var previewExtension = ZipGuard.ImageExtensionFromMagic(ZipGuard.ReadHead(previewTemp));
            if (previewExtension is null)
            {
                errors.Add("The preview is not a PNG, JPEG, GIF or WebP image.");
            }
            else if (new FileInfo(previewTemp).Length > ZipGuard.MaxPreviewBytes)
            {
                errors.Add($"The preview is larger than {ZipGuard.MaxPreviewBytes / (1024 * 1024)} MB.");
            }
            else
            {
                manifest.Preview = "preview" + previewExtension;
                File.Copy(previewTemp, Path.Combine(itemDir, manifest.Preview), overwrite: true);
            }
        }
        else if (existing?.Preview is not null && existingFolder is not null)
        {
            var previous = Path.Combine(existingFolder.Path, existing.Preview);
            if (File.Exists(previous))
            {
                manifest.Preview = existing.Preview;
                File.Copy(previous, Path.Combine(itemDir, existing.Preview), overwrite: true);
            }
        }

        if (errors.Count > 0)
        {
            return;
        }

        var readmeText = readme.Length > 0
            ? readme
            : existing is not null && existingFolder is not null && File.Exists(Path.Combine(existingFolder.Path, "README.md"))
                ? File.ReadAllText(Path.Combine(existingFolder.Path, "README.md"))
                : $"# {name}\n\n{description}\n";
        File.WriteAllText(Path.Combine(itemDir, "README.md"), readmeText.Replace("\r\n", "\n").TrimEnd() + "\n");
        manifest.Save(Path.Combine(itemDir, "manifest.json"));

        var packageDir = Path.Combine(work, "package");
        Directory.CreateDirectory(packageDir);
        File.Copy(packagePath, Path.Combine(packageDir, assetName), overwrite: true);

        result["id"] = id;
        result["tag"] = id;
        result["name"] = name;
        result["assetName"] = assetName;
        result["version"] = version;
        result["downloadedName"] = downloadedName;
        Console.Error.WriteLine($"Accepted {id} v{version} ({report.FormatKind} v{report.FormatVersion}, {manifest.Package.SizeBytes} bytes).");
    }

    private static bool LooksLikeImage(string name)
    {
        var extension = Path.GetExtension(name).ToLowerInvariant();
        return extension is ".png" or ".jpg" or ".jpeg" or ".gif" or ".webp";
    }

    private static string BumpPatch(string version)
    {
        var parts = version.Split('.');
        if (parts.Length == 3 && int.TryParse(parts[2], out var patch))
        {
            return $"{parts[0]}.{parts[1]}.{patch + 1}";
        }

        return "1.0.1";
    }

    /// <summary>The extension's own file extension for what the package turned out to be.</summary>
    private static string CanonicalExtension(PackageReport report)
    {
        switch (report.Kind)
        {
            case ItemKind.NotificationStyle:
                return report.Contents["frameStyle"]?.GetValue<bool>() == true ? ".pastyle" : ".panotif";
            case ItemKind.ScreenshotFrame:
                return ".paframe";
            case ItemKind.ShowcasePage:
                return ".pashowcase";
            case ItemKind.UnlockSounds:
                return ".pasounds";
            case ItemKind.Theme:
                return ".patheme";
            default:
                return ".pa";
        }
    }
}
