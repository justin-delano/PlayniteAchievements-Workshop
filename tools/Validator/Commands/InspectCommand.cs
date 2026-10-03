using System.Text.Json;
using System.Text.Json.Nodes;
using Workshop.Validator.Cli;
using Workshop.Validator.Formats;
using Workshop.Validator.Model;

namespace Workshop.Validator.Commands;

/// <summary>Prints what the validator sees in a package: kind, format, contents, errors.</summary>
public static class InspectCommand
{
    public static int Run(Args args)
    {
        var path = args.Positional.FirstOrDefault() ?? args.Get("package")
                   ?? throw new ValidationException("usage: inspect <package>");
        var report = PackageInspector.Inspect(path);
        var output = new JsonObject
        {
            ["ok"] = report.Ok,
            ["kind"] = report.Kind?.ToString(),
            ["formatKind"] = report.FormatKind,
            ["formatVersion"] = report.FormatVersion,
            ["contents"] = report.Contents,
            ["game"] = report.Game is null ? null : JsonSerializer.SerializeToNode(report.Game, Manifest.JsonOptions),
            ["errors"] = new JsonArray(report.Errors.Select(e => (JsonNode?)e).ToArray()),
            ["warnings"] = new JsonArray(report.Warnings.Select(e => (JsonNode?)e).ToArray()),
            ["sha256"] = File.Exists(path) && ZipGuard.IsZip(path) ? ZipGuard.Sha256Hex(path) : null,
            ["sizeBytes"] = File.Exists(path) ? new FileInfo(path).Length : 0
        };
        Console.WriteLine(output.ToJsonString(Manifest.JsonOptions));
        return report.Ok ? 0 : 1;
    }
}
