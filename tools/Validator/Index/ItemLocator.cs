using Workshop.Validator.Model;

namespace Workshop.Validator.Index;

public sealed record ItemFolder(string Id, string Path, ItemKind Kind);

/// <summary>Finds every item folder: a type folder's children, two levels down for game-data.</summary>
public static class ItemLocator
{
    public static IReadOnlyList<ItemFolder> All(string root)
    {
        var items = new List<ItemFolder>();
        foreach (var folder in Kinds.AllFolders)
        {
            var kind = Kinds.FromFolder(folder)!.Value;
            var typePath = Path.Combine(root, folder);
            if (!Directory.Exists(typePath))
            {
                continue;
            }

            if (kind == ItemKind.GameCustomData)
            {
                foreach (var gamePath in Directory.GetDirectories(typePath).OrderBy(p => p, StringComparer.Ordinal))
                {
                    foreach (var itemPath in Directory.GetDirectories(gamePath).OrderBy(p => p, StringComparer.Ordinal))
                    {
                        AddIfItem(items, root, itemPath, kind);
                    }
                }
            }
            else
            {
                foreach (var itemPath in Directory.GetDirectories(typePath).OrderBy(p => p, StringComparer.Ordinal))
                {
                    AddIfItem(items, root, itemPath, kind);
                }
            }
        }

        return items;
    }

    public static ItemFolder? FromRelativePath(string root, string relativePath)
    {
        var id = relativePath.Replace('\\', '/').Trim('/');
        var segments = id.Split('/');
        var kind = segments.Length > 0 ? Kinds.FromFolder(segments[0]) : null;
        if (kind is null)
        {
            return null;
        }

        var expected = kind == ItemKind.GameCustomData ? 3 : 2;
        if (segments.Length != expected)
        {
            return null;
        }

        return new ItemFolder(id, Path.Combine(root, id.Replace('/', Path.DirectorySeparatorChar)), kind.Value);
    }

    private static void AddIfItem(List<ItemFolder> items, string root, string itemPath, ItemKind kind)
    {
        if (!File.Exists(Path.Combine(itemPath, "manifest.json")))
        {
            return;
        }

        var id = Path.GetRelativePath(root, itemPath).Replace('\\', '/');
        items.Add(new ItemFolder(id, itemPath, kind));
    }
}
