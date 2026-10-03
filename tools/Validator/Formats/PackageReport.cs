using System.Text.Json.Nodes;
using Workshop.Validator.Model;

namespace Workshop.Validator.Formats;

/// <summary>What the validator learned about one package file.</summary>
public sealed class PackageReport
{
    public ItemKind? Kind { get; set; }

    public string FormatKind { get; set; } = "";

    public int FormatVersion { get; set; }

    /// <summary>The manifest's `contents`: counts of what the package customizes.</summary>
    public JsonObject Contents { get; set; } = new();

    /// <summary>For game data, the game the package was exported for.</summary>
    public GameInfo? Game { get; set; }

    public List<string> Errors { get; } = new();

    public List<string> Warnings { get; } = new();

    public bool Ok => Errors.Count == 0;

    public void Error(string message) => Errors.Add(message);

    public void Warn(string message) => Warnings.Add(message);
}
