using Workshop.Validator.Cli;
using Workshop.Validator.Model;

namespace Workshop.Validator.Commands;

/// <summary>Stamps the uploaded release asset's URL into a prepared item manifest.</summary>
public static class FinalizeCommand
{
    public static int Run(Args args)
    {
        var itemDir = args.Require("item");
        var url = args.Require("asset-url").Trim();
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.Scheme != "https")
        {
            throw new ValidationException($"'{url}' is not an https URL.");
        }

        var manifestPath = Path.Combine(itemDir, "manifest.json");
        var manifest = Manifest.Load(manifestPath);
        manifest.Package.Release.Url = url;
        manifest.Save(manifestPath);
        Console.WriteLine($"{manifest.Id}: release asset {url}");
        return 0;
    }
}
