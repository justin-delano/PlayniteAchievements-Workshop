using System.Text;
using Workshop.Validator.Model;

namespace Workshop.Validator.Index;

/// <summary>
/// The image block at the top of an item's README: the cover, then the preview, as relative
/// markdown images between two comment markers. Intake writes it and build-index rewrites it
/// from the manifest, so the block always matches the item's images and never repeats.
/// </summary>
public static class ItemReadme
{
    public const string StartMarker = "<!-- workshop:images -->";
    public const string EndMarker = "<!-- /workshop:images -->";

    /// <summary>The README text with any previous image block replaced by one for <paramref name="manifest"/>.</summary>
    public static string WithImages(string readme, Manifest manifest)
    {
        var body = WithoutImages(readme).Trim('\n');
        var images = new List<string>();
        if (manifest.Cover is not null)
        {
            images.Add($"![Cover]({manifest.Cover})");
        }

        if (manifest.Preview is not null)
        {
            images.Add($"![Preview]({manifest.Preview})");
        }

        var sb = new StringBuilder();
        if (images.Count > 0)
        {
            sb.Append(StartMarker).Append('\n');
            sb.Append(string.Join("\n\n", images)).Append('\n');
            sb.Append(EndMarker).Append('\n');
            if (body.Length > 0)
            {
                sb.Append('\n');
            }
        }

        if (body.Length > 0)
        {
            sb.Append(body.TrimEnd()).Append('\n');
        }

        return sb.ToString();
    }

    /// <summary>The README text with LF line endings and every image block removed.</summary>
    public static string WithoutImages(string readme)
    {
        var text = readme.Replace("\r\n", "\n");
        while (true)
        {
            var start = text.IndexOf(StartMarker, StringComparison.Ordinal);
            if (start < 0)
            {
                return text;
            }

            var end = text.IndexOf(EndMarker, start, StringComparison.Ordinal);
            var cut = end < 0 ? start + StartMarker.Length : end + EndMarker.Length;
            text = text[..start] + text[cut..];
        }
    }
}
