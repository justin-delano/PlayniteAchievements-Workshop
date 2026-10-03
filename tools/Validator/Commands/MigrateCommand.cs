using System.Text.Json.Nodes;
using Workshop.Validator.Cli;
using Workshop.Validator.Index;
using Workshop.Validator.Model;
using Workshop.Validator.Schema.Migrations;

namespace Workshop.Validator.Commands;

/// <summary>Rewrites every manifest to the current schema; prints the ids it changed.</summary>
public static class MigrateCommand
{
    public static int Run(Args args)
    {
        var root = args.Get("root") ?? ".";
        var changed = 0;
        foreach (var item in ItemLocator.All(root))
        {
            var path = Path.Combine(item.Path, "manifest.json");
            var node = JsonNode.Parse(File.ReadAllText(path)) as JsonObject
                       ?? throw new ValidationException($"{item.Id}: manifest.json is not an object.");
            if (!MigrationRunner.Upgrade(node))
            {
                continue;
            }

            // Round-trip through the typed model so field order and formatting are canonical.
            var manifest = System.Text.Json.JsonSerializer.Deserialize<Manifest>(node.ToJsonString(), Manifest.JsonOptions)
                           ?? throw new ValidationException($"{item.Id}: migrated manifest could not be read back.");
            manifest.Save(path);
            Console.WriteLine($"migrated {item.Id}");
            changed++;
        }

        Console.WriteLine(changed == 0 ? "Every manifest is at the current schema." : $"{changed} manifest(s) migrated.");
        return 0;
    }
}
