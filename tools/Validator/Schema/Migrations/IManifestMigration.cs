using System.Text.Json.Nodes;

namespace Workshop.Validator.Schema.Migrations;

/// <summary>
/// Rewrites a manifest from <see cref="From"/> to <see cref="From"/> + 1. Migrations work on the
/// raw JSON so a field the current <c>Manifest</c> class no longer has can still be read and
/// moved. Add one class per schema bump and register it in <see cref="MigrationRunner.All"/>.
/// </summary>
public interface IManifestMigration
{
    int From { get; }

    void Apply(JsonObject manifest);
}

public static class MigrationRunner
{
    public static readonly IReadOnlyList<IManifestMigration> All = new List<IManifestMigration>
    {
        // new V1ToV2(),
    };

    /// <summary>Applies every migration from the manifest's version up to the current one. Returns true when it changed.</summary>
    public static bool Upgrade(JsonObject manifest)
    {
        var version = manifest["schemaVersion"]?.GetValue<int>() ?? 1;
        var changed = false;
        while (version < Model.ManifestSchema.Current)
        {
            var migration = All.FirstOrDefault(m => m.From == version)
                ?? throw new Cli.ValidationException($"No migration from manifest schema {version} to {version + 1}.");
            migration.Apply(manifest);
            version++;
            manifest["schemaVersion"] = version;
            changed = true;
        }

        return changed;
    }
}
