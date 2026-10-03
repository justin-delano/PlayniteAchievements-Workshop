namespace Workshop.Validator.Model;

/// <summary>The item kinds the Workshop lists, with their folder, package format and extension.</summary>
public enum ItemKind
{
    Colors,
    NotificationStyle,
    ScreenshotFrame,
    ShowcasePage,
    UnlockSounds,
    Theme,
    GameCustomData
}

public static class Kinds
{
    // Format discriminators written by the extension into each package's manifest entry.
    public const string ColorsFormat = "PlayniteAchievements.Colors";
    public const string NotificationStyleFormat = "PlayniteAchievements.NotificationStyle";
    public const string ShowcasePageFormat = "PlayniteAchievements.ShowcasePage";
    public const string GameCustomDataFormat = "PlayniteAchievements.GameCustomData";
    public const string UnlockSoundsFormat = "PlayniteAchievements.UnlockSounds";
    public const string ThemeFormat = "PlayniteAchievements.Theme";

    // Manifest entry names inside each package. The style manifest's name predates the split
    // into .panotif/.paframe and stays for compatibility.
    public const string ColorsManifest = "colors.json";
    public const string NotificationStyleManifest = "notification-style.pastyle";
    public const string ShowcasePageManifest = "showcase-page.json";
    public const string GameCustomDataManifest = "custom-data.pa";
    public const string CustomAchievementsCsv = "custom-achievements.csv";
    public const string UnlockSoundsManifest = "unlock-sounds.json";
    public const string ThemeManifest = "theme.json";

    // Newest format version of each package type the validator understands. A package from a
    // newer extension is refused until the validator is updated.
    public const int ColorsMaxVersion = 1;
    public const int NotificationStyleMaxVersion = 3;
    public const int ShowcasePageMaxVersion = 2;
    public const int GameCustomDataMaxSchema = 8;
    public const int UnlockSoundsMaxVersion = 1;
    public const int ThemeMaxVersion = 1;

    public static readonly string[] AllFolders =
    {
        "colors", "notification-styles", "screenshot-frames", "showcase-pages", "sound-packs", "themes", "game-data"
    };

    public static string Folder(ItemKind kind) => kind switch
    {
        ItemKind.Colors => "colors",
        ItemKind.NotificationStyle => "notification-styles",
        ItemKind.ScreenshotFrame => "screenshot-frames",
        ItemKind.ShowcasePage => "showcase-pages",
        ItemKind.UnlockSounds => "sound-packs",
        ItemKind.Theme => "themes",
        ItemKind.GameCustomData => "game-data",
        _ => throw new ArgumentOutOfRangeException(nameof(kind))
    };

    public static ItemKind? FromFolder(string folder) => folder switch
    {
        "colors" => ItemKind.Colors,
        "notification-styles" => ItemKind.NotificationStyle,
        "screenshot-frames" => ItemKind.ScreenshotFrame,
        "showcase-pages" => ItemKind.ShowcasePage,
        "sound-packs" => ItemKind.UnlockSounds,
        "themes" => ItemKind.Theme,
        "game-data" => ItemKind.GameCustomData,
        _ => null
    };

    public static string DisplayName(ItemKind kind) => kind switch
    {
        ItemKind.Colors => "Color sets",
        ItemKind.NotificationStyle => "Notification styles",
        ItemKind.ScreenshotFrame => "Screenshot frames",
        ItemKind.ShowcasePage => "Showcase pages",
        ItemKind.UnlockSounds => "Unlock sound packs",
        ItemKind.Theme => "Themes",
        ItemKind.GameCustomData => "Per-game custom data",
        _ => kind.ToString()
    };

    /// <summary>The issue form's dropdown labels.</summary>
    public static ItemKind? FromFormLabel(string? label) => label?.Trim() switch
    {
        "Colors" => ItemKind.Colors,
        "Notification style" => ItemKind.NotificationStyle,
        "Screenshot frame" => ItemKind.ScreenshotFrame,
        "Showcase page" => ItemKind.ShowcasePage,
        "Unlock sound pack" => ItemKind.UnlockSounds,
        "Theme" => ItemKind.Theme,
        "Per-game custom data" => ItemKind.GameCustomData,
        _ => null
    };

    /// <summary>Package extensions accepted for a kind (lowercase, with the dot).</summary>
    public static string[] Extensions(ItemKind kind) => kind switch
    {
        ItemKind.Colors => new[] { ".pacolors" },
        ItemKind.NotificationStyle => new[] { ".panotif" },
        ItemKind.ScreenshotFrame => new[] { ".paframe" },
        ItemKind.ShowcasePage => new[] { ".pashowcase" },
        ItemKind.UnlockSounds => new[] { ".pasounds" },
        ItemKind.Theme => new[] { ".patheme" },
        ItemKind.GameCustomData => new[] { ".pa" },
        _ => Array.Empty<string>()
    };

    public static readonly string[] AllPackageExtensions =
    {
        ".pacolors", ".panotif", ".paframe", ".pashowcase", ".pasounds", ".patheme", ".pa"
    };

    /// <summary>
    /// The oldest extension version that reads each format version. Updated when a format
    /// changes; the index exposes it so older extensions grey out items they cannot import.
    /// </summary>
    public static string MinPluginVersion(ItemKind kind, int formatVersion) => kind switch
    {
        // Color sets, sound packs, themes and game keys in .pa files arrived with the Workshop release.
        ItemKind.Colors or ItemKind.UnlockSounds or ItemKind.Theme => "4.1.0",
        ItemKind.GameCustomData => "4.1.0",
        _ => "4.0.0"
    };
}
