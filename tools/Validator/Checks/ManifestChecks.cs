using System.Globalization;
using System.Text.RegularExpressions;
using Workshop.Validator.Index;
using Workshop.Validator.Model;

namespace Workshop.Validator.Checks;

/// <summary>
/// The structural rules of schema/manifest.v1.schema.json, in code so the validator needs no
/// JSON Schema library. Keep the two in step.
/// </summary>
public static partial class ManifestChecks
{
    public const int MaxNameLength = 80;
    public const int MaxDescriptionLength = 400;
    public const int MaxAuthorLength = 40;
    public const int MaxTags = 10;
    public static readonly string[] Licenses = { "CC-BY-4.0", "CC0-1.0" };

    public static List<string> Run(Manifest manifest, ItemFolder item)
    {
        var errors = new List<string>();

        if (manifest.SchemaVersion != ManifestSchema.Current)
        {
            errors.Add($"schemaVersion is {manifest.SchemaVersion}; the current schema is {ManifestSchema.Current}. Run the migrate workflow.");
        }

        if (manifest.Id != item.Id)
        {
            errors.Add($"id '{manifest.Id}' does not match the folder '{item.Id}'.");
        }

        if (!IdPattern().IsMatch(manifest.Id))
        {
            errors.Add($"id '{manifest.Id}' is not lowercase kebab-case under a known type folder.");
        }

        if (manifest.Kind != item.Kind)
        {
            errors.Add($"kind '{manifest.Kind}' does not belong in the '{item.Id.Split('/')[0]}' folder.");
        }

        CheckLength(errors, "name", manifest.Name, MaxNameLength);
        CheckLength(errors, "description", manifest.Description, MaxDescriptionLength);
        CheckLength(errors, "author", manifest.Author, MaxAuthorLength);

        if (manifest.AuthorGitHub is not null && !LoginPattern().IsMatch(manifest.AuthorGitHub))
        {
            errors.Add("authorGitHub is not a valid GitHub login.");
        }

        if (manifest.OwnerHash is not null && !Sha256Pattern().IsMatch(manifest.OwnerHash))
        {
            errors.Add("ownerHash is not a lowercase SHA-256 hex string.");
        }

        if (manifest.AuthorGitHub is null && manifest.OwnerHash is null)
        {
            errors.Add("Either authorGitHub or ownerHash must be set so the item can be updated by its owner.");
        }

        foreach (var maintainer in manifest.Maintainers)
        {
            if (!LoginPattern().IsMatch(maintainer))
            {
                errors.Add($"maintainer '{maintainer}' is not a valid GitHub login.");
            }
        }

        if (!SemVerPattern().IsMatch(manifest.Version))
        {
            errors.Add($"version '{manifest.Version}' is not MAJOR.MINOR.PATCH.");
        }

        if (!Licenses.Contains(manifest.License))
        {
            errors.Add($"license '{manifest.License}' must be one of {string.Join(", ", Licenses)}.");
        }

        if (manifest.Tags.Count > MaxTags)
        {
            errors.Add($"At most {MaxTags} tags are allowed.");
        }

        foreach (var tag in manifest.Tags)
        {
            if (!TagPattern().IsMatch(tag))
            {
                errors.Add($"tag '{tag}' is not lowercase kebab-case.");
            }
        }

        if (!SemVerPattern().IsMatch(manifest.MinPluginVersion))
        {
            errors.Add($"minPluginVersion '{manifest.MinPluginVersion}' is not MAJOR.MINOR.PATCH.");
        }

        CheckDate(errors, "created", manifest.Created);
        CheckDate(errors, "updated", manifest.Updated);

        if (manifest.Kind == ItemKind.GameCustomData)
        {
            if (manifest.Game is null || manifest.Game.Keys.Count == 0 || string.IsNullOrWhiteSpace(manifest.Game.Name))
            {
                errors.Add("game-data items need a game with at least one key.");
            }
            else
            {
                var expectedFolder = manifest.Game.Keys[0].FolderKey();
                var actualFolder = item.Id.Split('/')[1];
                if (expectedFolder != actualFolder)
                {
                    errors.Add($"The game folder '{actualFolder}' does not match the first game key ('{expectedFolder}').");
                }
            }
        }
        else if (manifest.Game is not null)
        {
            errors.Add("Only game-data items carry a game.");
        }

        if (string.IsNullOrWhiteSpace(manifest.Package.File) || manifest.Package.File.Contains('/') || manifest.Package.File.Contains('\\'))
        {
            errors.Add("package.file must be a bare file name.");
        }

        if (string.IsNullOrWhiteSpace(manifest.Package.FormatKind))
        {
            errors.Add("package.formatKind is required.");
        }

        if (manifest.Package.SizeBytes <= 0)
        {
            errors.Add("package.sizeBytes must be positive.");
        }

        if (!Sha256Pattern().IsMatch(manifest.Package.Sha256))
        {
            errors.Add("package.sha256 is not a lowercase SHA-256 hex string.");
        }

        if (manifest.Package.Release.Tag != manifest.Id)
        {
            errors.Add($"package.release.tag '{manifest.Package.Release.Tag}' must equal the id.");
        }

        if (!Uri.TryCreate(manifest.Package.Release.Url, UriKind.Absolute, out var url) || url.Scheme != "https")
        {
            errors.Add("package.release.url must be an https URL.");
        }

        if (manifest.Readme != "README.md")
        {
            errors.Add("readme must be 'README.md'.");
        }

        if (manifest.Preview is not null && (manifest.Preview.Contains('/') || manifest.Preview.Contains('\\')))
        {
            errors.Add("preview must be a bare file name.");
        }

        if (manifest.Cover is not null && (manifest.Cover.Contains('/') || manifest.Cover.Contains('\\')))
        {
            errors.Add("cover must be a bare file name.");
        }

        return errors;
    }

    private static void CheckLength(List<string> errors, string field, string value, int max)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            errors.Add($"{field} is required.");
        }
        else if (value.Length > max)
        {
            errors.Add($"{field} is longer than {max} characters.");
        }
    }

    private static void CheckDate(List<string> errors, string field, string value)
    {
        if (!DateTime.TryParseExact(value, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out _))
        {
            errors.Add($"{field} '{value}' is not a yyyy-MM-dd date.");
        }
    }

    [GeneratedRegex(@"^(colors|notifications|frames|showcase-pages|sounds|bundles)/[a-z0-9]+(-[a-z0-9]+)*$|^game-data/[a-z0-9]+(-[a-z0-9]+)*/[a-z0-9]+(-[a-z0-9]+)*$")]
    private static partial Regex IdPattern();

    [GeneratedRegex(@"^[A-Za-z0-9](?:[A-Za-z0-9]|-(?=[A-Za-z0-9])){0,38}$")]
    public static partial Regex LoginPattern();

    [GeneratedRegex(@"^[a-f0-9]{64}$")]
    public static partial Regex Sha256Pattern();

    [GeneratedRegex(@"^\d+\.\d+\.\d+$")]
    public static partial Regex SemVerPattern();

    [GeneratedRegex(@"^[a-z0-9]+(-[a-z0-9]+)*$")]
    public static partial Regex TagPattern();
}
