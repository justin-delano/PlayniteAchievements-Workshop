using System.IO.Compression;
using System.Text.Json.Nodes;
using PlayniteAchievements.Services.UI;
using Workshop.Validator.Model;

namespace Workshop.Validator.Formats;

/// <summary>
/// Opens a package, recognizes its format by the manifest entry it carries, checks it the way
/// the extension's importer would, and computes the manifest's `contents`. One static entry
/// point; one private method per format.
/// </summary>
public static class PackageInspector
{
    public static PackageReport Inspect(string path, ItemKind? expectedKind = null)
    {
        var report = new PackageReport();
        if (!File.Exists(path))
        {
            report.Error("The package file is missing.");
            return report;
        }

        if (!ZipGuard.IsZip(path))
        {
            report.Error("The package is not a zip file. Submit the file the extension exported (renamed to .zip if needed), not a file extracted from it.");
            return report;
        }

        using var archive = ZipFile.OpenRead(path);
        var entries = ZipGuard.Index(archive, report.Errors);
        if (!report.Ok)
        {
            return report;
        }

        if (entries.ContainsKey(Kinds.ThemeManifest))
        {
            InspectTheme(entries, report);
        }
        else if (entries.ContainsKey(Kinds.ColorsManifest))
        {
            InspectColors(entries, report);
        }
        else if (entries.ContainsKey(Kinds.NotificationStyleManifest))
        {
            InspectNotificationStyle(entries, report);
        }
        else if (entries.ContainsKey(Kinds.ShowcasePageManifest))
        {
            InspectShowcasePage(entries, report);
        }
        else if (entries.ContainsKey(Kinds.UnlockSoundsManifest))
        {
            InspectUnlockSounds(entries, report);
        }
        else if (entries.ContainsKey(Kinds.GameCustomDataManifest) || entries.ContainsKey(Kinds.CustomAchievementsCsv))
        {
            InspectGameCustomData(entries, report);
        }
        else
        {
            report.Error("The zip does not contain a PlayniteAchievements package manifest.");
            return report;
        }

        if (expectedKind is not null && report.Kind is not null && report.Kind != expectedKind)
        {
            // A toast-only style submitted under "Screenshot frame" (or the reverse) is a form
            // mistake worth catching; everything else is a different format entirely.
            report.Error($"The form says '{Kinds.DisplayName(expectedKind.Value)}' but the package is a {Kinds.DisplayName(report.Kind.Value).ToLowerInvariant()} package.");
        }

        return report;
    }

    // ---- Colors --------------------------------------------------------------------------

    private static void InspectColors(IReadOnlyDictionary<string, ZipArchiveEntry> entries, PackageReport report)
    {
        var manifest = ZipGuard.ReadJsonObject(entries[Kinds.ColorsManifest], report.Errors);
        if (manifest is null || !ExpectKind(manifest, Kinds.ColorsFormat, report))
        {
            return;
        }

        var version = manifest["Version"]?.GetValue<int>() ?? 0;
        if (version > Kinds.ColorsMaxVersion)
        {
            report.Error($"The color set is version {version}, newer than this validator understands ({Kinds.ColorsMaxVersion}).");
            return;
        }

        CheckColors(manifest, report);

        report.Kind = ItemKind.Colors;
        report.FormatKind = Kinds.ColorsFormat;
        report.FormatVersion = version;
        report.Contents = new JsonObject
        {
            ["rarityColors"] = manifest["RarityColors"] is JsonObject,
            ["providerColors"] = (manifest["ProviderColorOverrides"] as JsonObject)?.Count ?? 0,
            ["resourceOverrides"] = (manifest["ResourceOverrides"] as JsonObject)?.Count ?? 0
        };
    }

    // ---- Notification style / screenshot frame ------------------------------------------

    private static void InspectNotificationStyle(IReadOnlyDictionary<string, ZipArchiveEntry> entries, PackageReport report)
    {
        var manifest = ZipGuard.ReadJsonObject(entries[Kinds.NotificationStyleManifest], report.Errors);
        if (manifest is null)
        {
            return;
        }

        if (!ExpectKind(manifest, Kinds.NotificationStyleFormat, report))
        {
            return;
        }

        var version = manifest["Version"]?.GetValue<int>() ?? 0;
        if (version > Kinds.NotificationStyleMaxVersion)
        {
            report.Error($"The style file is version {version}, newer than this validator understands ({Kinds.NotificationStyleMaxVersion}). The Workshop tooling needs an update.");
            return;
        }

        var hasToast = manifest["HasToast"]?.GetValue<bool>() ?? true;
        var hasFrame = manifest["HasFrame"]?.GetValue<bool>() ?? true;
        var hasToastTemplate = entries.ContainsKey("template-toast.xaml");
        var hasFrameTemplate = entries.ContainsKey("template-frame.xaml");

        foreach (var templateEntry in new[] { "template-toast.xaml", "template-frame.xaml" })
        {
            if (entries.TryGetValue(templateEntry, out var entry) &&
                !XamlTemplateSanitizer.TryValidate(ZipGuard.ReadText(entry), out var error))
            {
                report.Error($"{templateEntry}: {error}");
            }
        }

        var images = CheckFlatImages(entries, "images", report);
        var style = manifest["Style"] as JsonObject;
        var kindStyles = (style?["KindStyles"] as JsonObject)?.Count ?? 0;

        report.Kind = hasToast ? ItemKind.NotificationStyle : ItemKind.ScreenshotFrame;
        report.FormatKind = Kinds.NotificationStyleFormat;
        report.FormatVersion = version;
        report.Contents = new JsonObject
        {
            ["toastStyle"] = hasToast,
            ["frameStyle"] = hasFrame,
            ["toastTemplate"] = hasToastTemplate,
            ["frameTemplate"] = hasFrameTemplate,
            ["images"] = images,
            ["kindStyles"] = kindStyles
        };
    }

    // ---- Showcase page -------------------------------------------------------------------

    private static void InspectShowcasePage(IReadOnlyDictionary<string, ZipArchiveEntry> entries, PackageReport report)
    {
        var manifest = ZipGuard.ReadJsonObject(entries[Kinds.ShowcasePageManifest], report.Errors);
        if (manifest is null || !ExpectKind(manifest, Kinds.ShowcasePageFormat, report))
        {
            return;
        }

        var version = manifest["Version"]?.GetValue<int>() ?? 0;
        if (version > Kinds.ShowcasePageMaxVersion)
        {
            report.Error($"The showcase file is version {version}, newer than this validator understands ({Kinds.ShowcasePageMaxVersion}).");
            return;
        }

        if (manifest["Page"] is not JsonObject page)
        {
            report.Error("The showcase file has no page.");
            return;
        }

        var widgets = (manifest["Widgets"] as JsonArray)?.Count ?? 0;
        var blocks = (page["Blocks"] as JsonArray)?.Count ?? 0;
        var achievementGrids = (manifest["AchievementGridSurfaces"] as JsonObject)?.Count ?? 0;
        var gameGrids = (manifest["GameGridSurfaces"] as JsonObject)?.Count ?? 0;
        var images = CheckFlatImages(entries, "images", report);

        report.Kind = ItemKind.ShowcasePage;
        report.FormatKind = Kinds.ShowcasePageFormat;
        report.FormatVersion = version;
        report.Contents = new JsonObject
        {
            ["widgets"] = widgets,
            ["blocks"] = blocks,
            ["achievementGrids"] = achievementGrids,
            ["gameGrids"] = gameGrids,
            ["images"] = images
        };
    }

    // ---- Unlock sounds -------------------------------------------------------------------

    private static void InspectUnlockSounds(IReadOnlyDictionary<string, ZipArchiveEntry> entries, PackageReport report)
    {
        var manifest = ZipGuard.ReadJsonObject(entries[Kinds.UnlockSoundsManifest], report.Errors);
        if (manifest is null || !ExpectKind(manifest, Kinds.UnlockSoundsFormat, report))
        {
            return;
        }

        var version = manifest["Version"]?.GetValue<int>() ?? 0;
        if (version > Kinds.UnlockSoundsMaxVersion)
        {
            report.Error($"The sound pack is version {version}, newer than this validator understands ({Kinds.UnlockSoundsMaxVersion}).");
            return;
        }

        var slots = new JsonArray();
        var tiers = new[] { "Common", "Uncommon", "Rare", "UltraRare", "Hidden", "Capstone" };
        foreach (var pair in manifest["Slots"] as JsonObject ?? new JsonObject())
        {
            var tier = tiers.FirstOrDefault(t => string.Equals(t, pair.Key, StringComparison.OrdinalIgnoreCase));
            if (tier is null)
            {
                report.Error($"The sound pack names an unknown tier '{pair.Key}'.");
                continue;
            }

            var entryName = ZipGuard.NormalizeEntryName(pair.Value?.GetValue<string>());
            if (!ZipGuard.IsFlatEntryUnder(entryName, "sounds"))
            {
                report.Error($"The sound for '{tier}' has an invalid path '{pair.Value}'.");
                continue;
            }

            var extension = Path.GetExtension(entryName!).ToLowerInvariant();
            if (!ZipGuard.AudioExtensions.Contains(extension))
            {
                report.Error($"The sound '{entryName}' is not a supported format (use .wav, .mp3 or .flac).");
                continue;
            }

            if (!entries.TryGetValue(entryName!, out var entry))
            {
                report.Error($"The sound pack is missing '{entryName}'.");
                continue;
            }

            if (!ZipGuard.IsAudio(ZipGuard.ReadHead(entry), extension))
            {
                report.Error($"The sound '{entryName}' is not a valid {extension} file.");
                continue;
            }

            slots.Add(tier);
        }

        if (slots.Count == 0 && report.Ok)
        {
            report.Error("The sound pack carries no sounds.");
        }

        report.Kind = ItemKind.UnlockSounds;
        report.FormatKind = Kinds.UnlockSoundsFormat;
        report.FormatVersion = version;
        report.Contents = new JsonObject { ["slots"] = slots, ["files"] = slots.Count };
    }

    // ---- Theme bundle --------------------------------------------------------------------

    private static void InspectTheme(IReadOnlyDictionary<string, ZipArchiveEntry> entries, PackageReport report)
    {
        var manifest = ZipGuard.ReadJsonObject(entries[Kinds.ThemeManifest], report.Errors);
        if (manifest is null || !ExpectKind(manifest, Kinds.ThemeFormat, report))
        {
            return;
        }

        var version = manifest["Version"]?.GetValue<int>() ?? 0;
        if (version > Kinds.ThemeMaxVersion)
        {
            report.Error($"The theme is version {version}, newer than this validator understands ({Kinds.ThemeMaxVersion}).");
            return;
        }

        var parts = new JsonArray();
        var contents = new JsonObject();
        var scratch = Path.Combine(Path.GetTempPath(), "workshop-validator", Guid.NewGuid().ToString("N"));
        try
        {
            foreach (var partNode in manifest["Parts"] as JsonArray ?? new JsonArray())
            {
                var part = partNode?.GetValue<string>() ?? "";
                switch (part)
                {
                    // Every part is an embedded standalone package, read by its own inspector.
                    case "Colors":
                    case "Sounds":
                    case "Toast":
                    case "Frame":
                        var entryName = part switch
                        {
                            "Colors" => "parts/colors.pacolors",
                            "Sounds" => "parts/sounds.pasounds",
                            "Toast" => "parts/toast.panotif",
                            _ => "parts/frame.paframe"
                        };
                        if (!entries.TryGetValue(entryName, out var nested))
                        {
                            report.Error($"The theme is missing its '{entryName}' part.");
                            break;
                        }

                        var nestedPath = ZipGuard.ExtractToTemp(nested, scratch);
                        var nestedReport = Inspect(nestedPath);
                        foreach (var error in nestedReport.Errors)
                        {
                            report.Error($"{entryName}: {error}");
                        }

                        parts.Add(part);
                        contents[part.ToLowerInvariant()] = nestedReport.Contents;
                        break;

                    default:
                        report.Error($"The theme names an unknown part '{part}'.");
                        break;
                }
            }
        }
        finally
        {
            try { Directory.Delete(scratch, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        }

        if (parts.Count == 0 && report.Ok)
        {
            report.Error("The theme carries no parts.");
        }

        report.Kind = ItemKind.Theme;
        report.FormatKind = Kinds.ThemeFormat;
        report.FormatVersion = version;
        contents["parts"] = parts;
        report.Contents = contents;
    }

    private static void CheckColors(JsonObject colors, PackageReport report)
    {
        static bool IsHex(string? value) =>
            value is not null && System.Text.RegularExpressions.Regex.IsMatch(value.Trim(), "^#(?:[0-9A-Fa-f]{6}|[0-9A-Fa-f]{8})$");

        if (colors["RarityColors"] is JsonObject rarity)
        {
            foreach (var pair in rarity)
            {
                if (pair.Value is JsonValue value && !IsHex(value.GetValue<string>()))
                {
                    report.Error($"The theme's rarity color '{pair.Key}' is not a #RRGGBB or #AARRGGBB value.");
                }
            }
        }

        foreach (var pair in colors["ProviderColorOverrides"] as JsonObject ?? new JsonObject())
        {
            if (!IsHex(pair.Value?.GetValue<string>()))
            {
                report.Error($"The theme's provider color for '{pair.Key}' is not a #RRGGBB or #AARRGGBB value.");
            }
        }

        foreach (var pair in colors["ResourceOverrides"] as JsonObject ?? new JsonObject())
        {
            var setting = pair.Value as JsonObject;
            var mode = setting?["Mode"]?.ToString();
            var custom = setting?["CustomValue"]?.GetValue<string>();
            // Mode may be serialized as a name or a number; only Custom values carry content.
            if ((mode == "Custom" || mode == "1") && !string.IsNullOrWhiteSpace(custom))
            {
                if (custom.Length > 128 || custom.Any(char.IsControl) || custom.Contains('<') || custom.Contains('{'))
                {
                    report.Error($"The theme's resource override '{pair.Key}' has an invalid value.");
                }
            }
        }
    }

    // ---- Per-game custom data ------------------------------------------------------------

    private static void InspectGameCustomData(IReadOnlyDictionary<string, ZipArchiveEntry> entries, PackageReport report)
    {
        report.Kind = ItemKind.GameCustomData;
        report.FormatKind = Kinds.GameCustomDataFormat;

        if (!entries.TryGetValue(Kinds.GameCustomDataManifest, out var manifestEntry))
        {
            // Custom achievements only: a CSV plus icons, merged by id on import.
            var csv = ZipGuard.ReadText(entries[Kinds.CustomAchievementsCsv]);
            var rows = csv.Split('\n').Count(line => line.Trim().Length > 0) - 1;
            if (rows <= 0)
            {
                report.Error("The custom achievements file has no rows.");
            }

            var csvImages = CheckFlatImages(entries, "images", report, allowAnyFolder: true);
            report.FormatVersion = Kinds.GameCustomDataMaxSchema;
            report.Contents = new JsonObject
            {
                ["customAchievements"] = Math.Max(0, rows),
                ["achievementIcons"] = csvImages,
                ["csvOnly"] = true
            };
            return;
        }

        var manifest = ZipGuard.ReadJsonObject(manifestEntry, report.Errors);
        if (manifest is null)
        {
            return;
        }

        var kind = manifest["Kind"]?.GetValue<string>();
        if (kind is not null && kind != Kinds.GameCustomDataFormat)
        {
            report.Error($"The package manifest says it is '{kind}', not per-game custom data.");
            return;
        }

        var schema = manifest["SchemaVersion"]?.GetValue<int>() ?? 7;
        if (schema > Kinds.GameCustomDataMaxSchema)
        {
            report.Error($"The .pa file is schema {schema}, newer than this validator understands ({Kinds.GameCustomDataMaxSchema}).");
            return;
        }

        CheckNoPersonalState(manifest, report);

        var overrides = manifest["AchievementOverrides"] as JsonObject ?? new JsonObject();
        var unlockedIcons = overrides.Count(p => (p.Value as JsonObject)?["UnlockedIconPath"] is not null)
                            + ((manifest["AchievementUnlockedIconOverrides"] as JsonObject)?.Count ?? 0);
        var lockedIcons = overrides.Count(p => (p.Value as JsonObject)?["LockedIconPath"] is not null)
                          + ((manifest["AchievementLockedIconOverrides"] as JsonObject)?.Count ?? 0);
        var notes = overrides.Count(p => !string.IsNullOrWhiteSpace((p.Value as JsonObject)?["Note"]?.GetValue<string>()))
                    + ((manifest["AchievementNotes"] as JsonObject)?.Count ?? 0);
        var customAchievements = (manifest["CustomAchievements"] as JsonArray)?.Count ?? 0;
        var categoryAssignments = overrides.Count(p => (p.Value as JsonObject)?["Category"] is not null)
                                  + ((manifest["AchievementCategoryOverrides"] as JsonObject)?.Count ?? 0);
        var categories = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var pair in overrides)
        {
            var category = (pair.Value as JsonObject)?["Category"]?.GetValue<string>();
            if (!string.IsNullOrWhiteSpace(category)) categories.Add(category);
        }

        foreach (var pair in manifest["AchievementCategoryOverrides"] as JsonObject ?? new JsonObject())
        {
            var category = pair.Value?.GetValue<string>();
            if (!string.IsNullOrWhiteSpace(category)) categories.Add(category);
        }

        foreach (var node in manifest["AchievementCategoryOrder"] as JsonArray ?? new JsonArray())
        {
            var category = node?.GetValue<string>();
            if (!string.IsNullOrWhiteSpace(category)) categories.Add(category);
        }

        var images = CheckFlatImages(entries, "images", report);

        report.FormatVersion = schema;
        report.Contents = new JsonObject
        {
            ["achievementIcons"] = unlockedIcons,
            ["lockedIcons"] = lockedIcons,
            ["customAchievements"] = customAchievements,
            ["achievementOverrides"] = overrides.Count,
            ["categoryAssignments"] = categoryAssignments,
            ["categories"] = categories.Count,
            ["categoryImages"] = (manifest["AchievementCategoryImageOverrides"] as JsonObject)?.Count ?? 0,
            ["capstones"] = (manifest["Capstones"] as JsonArray)?.Count ?? 0,
            ["notes"] = notes,
            ["achievementOrder"] = (manifest["AchievementOrder"] as JsonArray)?.Count > 0,
            ["notificationStyle"] = manifest["NotificationAppearanceOverride"] is JsonObject,
            ["manualLink"] = manifest["ManualLink"] is JsonObject,
            ["imageFiles"] = images
        };

        report.Game = ReadGame(manifest);
        if (report.Game is null)
        {
            report.Error("The .pa file carries no game keys. Export it again with the current extension, which records which game it belongs to.");
        }
    }

    /// <summary>
    /// A shared .pa file must carry curation only. The extension strips progress on export; a
    /// file assembled any other way is refused rather than silently cleaned, so nothing personal
    /// reaches the release.
    /// </summary>
    private static void CheckNoPersonalState(JsonObject manifest, PackageReport report)
    {
        if ((manifest["GoalAchievementApiNames"] as JsonArray)?.Count > 0)
        {
            report.Error("The .pa file contains personal goals. Export it again with Share to Workshop.");
        }

        if (manifest["ManualLink"] is JsonObject link &&
            (((link["UnlockStates"] as JsonObject)?.Count ?? 0) > 0 || ((link["UnlockTimes"] as JsonObject)?.Count ?? 0) > 0))
        {
            report.Error("The .pa file contains manual unlock progress. Export it again with Share to Workshop.");
        }

        foreach (var pair in manifest["AchievementOverrides"] as JsonObject ?? new JsonObject())
        {
            var entry = pair.Value as JsonObject;
            if (entry?["UnlockTimeUtc"] is not null || entry?["ClearUnlockTime"]?.GetValue<bool>() == true)
            {
                report.Error("The .pa file contains unlock times. Export it again with Share to Workshop.");
                break;
            }
        }

        foreach (var node in manifest["CustomAchievements"] as JsonArray ?? new JsonArray())
        {
            var entry = node as JsonObject;
            if (entry?["Unlocked"]?.GetValue<bool>() == true || entry?["UnlockTimeUtc"] is not null || entry?["ProgressNum"] is not null)
            {
                report.Error("The .pa file contains custom achievement progress. Export it again with Share to Workshop.");
                break;
            }
        }
    }

    private static GameInfo? ReadGame(JsonObject manifest)
    {
        if (manifest["GameKeys"] is not JsonArray keys || keys.Count == 0)
        {
            return null;
        }

        var game = new GameInfo();
        foreach (var node in keys.OfType<JsonObject>())
        {
            var key = new GameKey
            {
                ProviderKey = node["ProviderKey"]?.GetValue<string>(),
                ProviderPlatformKey = node["ProviderPlatformKey"]?.GetValue<string>(),
                ProviderGameId = node["ProviderGameId"]?.GetValue<int>(),
                ProviderGameKey = node["ProviderGameKey"]?.GetValue<string>(),
                Name = node["Name"]?.GetValue<string>(),
                Platform = node["Platform"]?.GetValue<string>()
            };
            game.Keys.Add(key);
            if (string.IsNullOrEmpty(game.Name) && !string.IsNullOrWhiteSpace(key.Name))
            {
                game.Name = key.Name;
            }

            game.Platform ??= key.Platform;
        }

        return game.Keys.Count == 0 ? null : game;
    }

    // ---- Shared ---------------------------------------------------------------------------

    private static bool ExpectKind(JsonObject manifest, string expected, PackageReport report)
    {
        var kind = manifest["Kind"]?.GetValue<string>();
        if (kind == expected)
        {
            return true;
        }

        report.Error($"The package manifest says it is '{kind ?? "(none)"}', expected '{expected}'.");
        return false;
    }

    /// <summary>Counts the image files under a folder, rejecting nested paths and non-image content.</summary>
    private static int CheckFlatImages(IReadOnlyDictionary<string, ZipArchiveEntry> entries, string folder, PackageReport report, bool allowAnyFolder = false)
    {
        var count = 0;
        foreach (var pair in entries)
        {
            var name = pair.Key;
            var isUnderFolder = name.StartsWith(folder + "/", StringComparison.OrdinalIgnoreCase);
            if (!isUnderFolder && !(allowAnyFolder && ZipGuard.ImageExtensionFromMagic(ZipGuard.ReadHead(pair.Value)) is not null))
            {
                continue;
            }

            if (isUnderFolder && !ZipGuard.IsFlatEntryUnder(name, folder))
            {
                report.Error($"The image path '{name}' is nested or unsafe; images must sit directly under '{folder}/'.");
                continue;
            }

            if (ZipGuard.ImageExtensionFromMagic(ZipGuard.ReadHead(pair.Value)) is null)
            {
                report.Error($"'{name}' is not a PNG, JPEG, GIF or WebP image.");
                continue;
            }

            count++;
        }

        return count;
    }
}
