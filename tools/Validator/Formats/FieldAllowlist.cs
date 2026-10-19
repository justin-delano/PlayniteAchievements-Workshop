using System.IO.Compression;
using System.Text.Json.Nodes;

namespace Workshop.Validator.Formats;

/// <summary>
/// Rejects any JSON field the official extension does not write. package-fields.json is a
/// byte-identical copy of the extension's WorkshopPackageFields.json, generated from its package
/// models; the drift workflow keeps the two in step. A type node maps field names to a value
/// descriptor: "value" for scalars, a type name for nested objects, {"list": d} or {"map": d}
/// for collections (map keys are data, not field names).
/// </summary>
public static class FieldAllowlist
{
    private const string ResourceName = "package-fields.json";
    private const string ScalarDescriptor = "value";

    private static readonly Lazy<JsonObject> Document = new(Load);

    /// <summary>Checks every manifest entry the allowlist knows, adding one error per unknown field.</summary>
    public static void Check(IReadOnlyDictionary<string, ZipArchiveEntry> entries, PackageReport report)
    {
        var roots = (JsonObject)Document.Value["roots"]!;
        foreach (var (entryName, rootType) in roots)
        {
            if (!entries.TryGetValue(entryName, out var entry))
            {
                continue;
            }

            var manifest = ZipGuard.ReadJsonObject(entry, report.Errors);
            if (manifest is not null)
            {
                CheckNode(manifest, rootType!, entryName, report);
            }
        }
    }

    private static void CheckNode(JsonNode? node, JsonNode descriptor, string path, PackageReport report)
    {
        if (node is null)
        {
            return;
        }

        if (descriptor is JsonObject collection)
        {
            if (collection["list"] is { } item && node is JsonArray array)
            {
                for (var i = 0; i < array.Count; i++)
                {
                    CheckNode(array[i], item, $"{path}[{i}]", report);
                }
            }
            else if (collection["map"] is { } value && node is JsonObject map)
            {
                foreach (var (key, child) in map)
                {
                    CheckNode(child, value, $"{path}[\"{key}\"]", report);
                }
            }

            return;
        }

        var typeName = descriptor.GetValue<string>();
        if (typeName == ScalarDescriptor || node is not JsonObject obj)
        {
            return;
        }

        var fields = (JsonObject)Document.Value["types"]![typeName]!;
        foreach (var (name, child) in obj)
        {
            if (!fields.TryGetPropertyValue(name, out var fieldDescriptor) || fieldDescriptor is null)
            {
                report.Error($"{path}: the field '{name}' is not part of the official package format.");
                continue;
            }

            CheckNode(child, fieldDescriptor, $"{path}.{name}", report);
        }
    }

    private static JsonObject Load()
    {
        using var stream = typeof(FieldAllowlist).Assembly.GetManifestResourceStream(ResourceName)
            ?? throw new InvalidOperationException($"The validator was built without {ResourceName}.");
        return (JsonObject)JsonNode.Parse(stream)!;
    }
}
