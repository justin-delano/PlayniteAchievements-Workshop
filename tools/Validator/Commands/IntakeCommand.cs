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
/// as a comment, success continues to the release upload and the commit to main.
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

        // Files: the first non-image attachment is the package; a package URL replaces it for
        // large files. Images are told apart by their markdown alt text, as the Worker writes
        // them: "preview" is the image the extension drew, "cover" the one the sharer chose. An
        // image without either (a hand-filled form) is the cover, since only the extension
        // draws previews.
        var attachments = form.Attachments(FilesField);
        var packageAttachment = attachments.FirstOrDefault(a => !a.IsImageLink && !LooksLikeImage(a.Name));
        var images = attachments.Where(a => a.IsImageLink || LooksLikeImage(a.Name)).ToList();
        var previewAttachment = images.FirstOrDefault(a => IsAlt(a, "preview"));
        var coverAttachment = images.FirstOrDefault(a => IsAlt(a, "cover"))
                              ?? images.FirstOrDefault(a => !IsAlt(a, "preview"));
        var packageUrl = form.Get(PackageUrlField).Trim();
        var sourceUrl = packageAttachment?.Url ?? (packageUrl.Length > 0 ? packageUrl : null);

        // An update without a package changes only the item's details: the package, its
        // version and everything read from it stay as published, so installs see no update.
        var metadataOnly = sourceUrl is null && existing is not null;
        if (sourceUrl is null && existing is null)
        {
            errors.Add("Attach the package file or fill in the package URL.");
        }

        if (errors.Count > 0)
        {
            return;
        }

        var downloadDir = Path.Combine(work, "download");
        using var downloader = new Downloader(Environment.GetEnvironmentVariable("GH_TOKEN") ?? Environment.GetEnvironmentVariable("GITHUB_TOKEN"));

        string? downloadedName = null;
        string? packagePath = null;
        PackageReport? report = null;
        if (!metadataOnly)
        {
            downloadedName = packageAttachment?.Name ?? Path.GetFileName(new Uri(sourceUrl!).AbsolutePath);
            packagePath = Path.Combine(downloadDir, "package.bin");
            await downloader.DownloadAsync(sourceUrl!, packagePath);

            report = PackageInspector.Inspect(packagePath, kind);
            foreach (var error in report.Errors)
            {
                errors.Add(error);
            }

            if (errors.Count > 0 || report.Kind is null)
            {
                return;
            }
        }

        var itemKind = metadataOnly ? existing!.Kind : report!.Kind!.Value;

        // Identity and version.
        var slug = existing is not null ? existing.Id.Split('/').Last() : Slug.From(name);
        string id;
        if (existing is not null)
        {
            id = existing.Id;
        }
        else if (itemKind == ItemKind.GameCustomData)
        {
            id = $"{Kinds.Folder(itemKind)}/{report!.Game!.Keys[0].FolderKey()}/{slug}";
        }
        else
        {
            id = $"{Kinds.Folder(itemKind)}/{slug}";
        }

        if (existing is null && Directory.Exists(Path.Combine(root, id.Replace('/', Path.DirectorySeparatorChar))))
        {
            throw new ValidationException($"An item with id `{id}` already exists. To update it, fill in the existing item id; otherwise choose a different name.");
        }

        var version = existing is null ? "1.0.0" : metadataOnly ? existing.Version : BumpPatch(existing.Version);
        var today = DateTime.UtcNow.ToString("yyyy-MM-dd");
        var assetName = metadataOnly ? "" : $"{slug}-{version}{CanonicalExtension(report!)}";

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
            Kind = itemKind,
            Name = name,
            Description = description,
            Author = author,
            AuthorGitHub = !isBotIssue && ManifestChecks.LoginPattern().IsMatch(login) ? login : existing?.AuthorGitHub,
            OwnerHash = submitterHash.Length > 0 ? submitterHash : existing?.OwnerHash,
            Maintainers = existing?.Maintainers ?? new List<string>(),
            Version = version,
            License = license,
            Tags = tags,
            MinPluginVersion = metadataOnly ? existing!.MinPluginVersion : Kinds.MinPluginVersion(itemKind, report!.FormatVersion),
            Created = existing?.Created ?? today,
            Updated = today,
            Game = metadataOnly ? existing!.Game : report!.Game,
            Contents = metadataOnly ? existing!.Contents : report!.Contents,
            Package = metadataOnly
                ? existing!.Package
                : new PackageInfo
                {
                    File = assetName,
                    FormatKind = report!.FormatKind,
                    FormatVersion = report.FormatVersion,
                    SizeBytes = new FileInfo(packagePath!).Length,
                    Sha256 = ZipGuard.Sha256Hex(packagePath!),
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

        manifest.Preview = await TakeImageAsync(downloader, previewAttachment, "preview", existing?.Preview, existingFolder, downloadDir, itemDir, errors);
        manifest.Cover = await TakeImageAsync(downloader, coverAttachment, "cover", existing?.Cover, existingFolder, downloadDir, itemDir, errors);

        if (errors.Count > 0)
        {
            return;
        }

        var readmeText = readme.Length > 0
            ? readme
            : existing is not null && existingFolder is not null && File.Exists(Path.Combine(existingFolder.Path, "README.md"))
                ? File.ReadAllText(Path.Combine(existingFolder.Path, "README.md"))
                : $"# {name}\n\n{description}\n";
        File.WriteAllText(Path.Combine(itemDir, "README.md"), ItemReadme.WithImages(readmeText, manifest));
        manifest.Save(Path.Combine(itemDir, "manifest.json"));

        if (!metadataOnly)
        {
            var packageDir = Path.Combine(work, "package");
            Directory.CreateDirectory(packageDir);
            File.Copy(packagePath!, Path.Combine(packageDir, assetName), overwrite: true);
        }

        result["id"] = id;
        result["tag"] = id;
        result["name"] = name;
        result["assetName"] = assetName;
        result["version"] = version;
        result["downloadedName"] = downloadedName;
        result["metadataOnly"] = metadataOnly;
        Console.Error.WriteLine(metadataOnly
            ? $"Accepted details of {id} v{version}; package unchanged."
            : $"Accepted {id} v{version} ({report!.FormatKind} v{report.FormatVersion}, {manifest.Package.SizeBytes} bytes).");
    }

    private static bool IsAlt(Attachment attachment, string alt) =>
        string.Equals(attachment.Name, alt, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Saves an attached image as <paramref name="role"/> plus its extension in the item folder
    /// and returns the file name. Without an attachment, an update keeps the previous image.
    /// </summary>
    private static async Task<string?> TakeImageAsync(
        Downloader downloader,
        Attachment? attachment,
        string role,
        string? previousFile,
        ItemFolder? existingFolder,
        string downloadDir,
        string itemDir,
        JsonArray errors)
    {
        if (attachment is null)
        {
            if (previousFile is null || existingFolder is null)
            {
                return null;
            }

            var previous = Path.Combine(existingFolder.Path, previousFile);
            if (!File.Exists(previous))
            {
                return null;
            }

            File.Copy(previous, Path.Combine(itemDir, previousFile), overwrite: true);
            return previousFile;
        }

        var temp = Path.Combine(downloadDir, role + ".bin");
        await downloader.DownloadAsync(attachment.Url, temp);
        var extension = ZipGuard.ImageExtensionFromMagic(ZipGuard.ReadHead(temp));
        if (extension is null)
        {
            errors.Add($"The {role} image is not a PNG, JPEG, GIF or WebP image.");
            return null;
        }

        if (new FileInfo(temp).Length > ZipGuard.MaxPreviewBytes)
        {
            errors.Add($"The {role} image is larger than {ZipGuard.MaxPreviewBytes / (1024 * 1024)} MB.");
            return null;
        }

        var file = role + extension;
        File.Copy(temp, Path.Combine(itemDir, file), overwrite: true);
        return file;
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
            case ItemKind.Colors:
                return ".pacolors";
            case ItemKind.NotificationStyle:
                return ".panotif";
            case ItemKind.ScreenshotFrame:
                return ".paframe";
            case ItemKind.ShowcasePage:
                return ".pashowcase";
            case ItemKind.UnlockSounds:
                return ".pasounds";
            case ItemKind.Bundle:
                return ".pabundle";
            default:
                return ".pa";
        }
    }
}
