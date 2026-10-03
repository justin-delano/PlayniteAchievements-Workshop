using System.Text.RegularExpressions;

namespace Workshop.Validator.Intake;

/// <summary>
/// Reads the markdown GitHub renders from an issue form: one <c>### Label</c> heading per field,
/// <c>_No response_</c> for an empty one, <c>- [x]</c> lines for checkboxes, and markdown links
/// for attached files.
/// </summary>
public sealed partial class IssueForm
{
    private readonly Dictionary<string, string> _fields = new(StringComparer.OrdinalIgnoreCase);

    public static IssueForm Parse(string body)
    {
        var form = new IssueForm();
        var lines = (body ?? string.Empty).Replace("\r\n", "\n").Split('\n');
        string? current = null;
        var buffer = new List<string>();
        foreach (var line in lines)
        {
            if (line.StartsWith("### ", StringComparison.Ordinal))
            {
                form.Flush(current, buffer);
                current = line[4..].Trim();
                buffer.Clear();
                continue;
            }

            if (current is not null)
            {
                buffer.Add(line);
            }
        }

        form.Flush(current, buffer);
        return form;
    }

    private void Flush(string? label, List<string> buffer)
    {
        if (label is null)
        {
            return;
        }

        var value = string.Join("\n", buffer).Trim();
        _fields[label] = value == "_No response_" ? string.Empty : value;
    }

    /// <summary>The field's text by its label, or empty when absent.</summary>
    public string Get(string label) => _fields.TryGetValue(label, out var value) ? value : string.Empty;

    /// <summary>True when a checkbox whose label starts with <paramref name="optionPrefix"/> is ticked.</summary>
    public bool IsChecked(string fieldLabel, string optionPrefix)
    {
        foreach (var line in Get(fieldLabel).Split('\n'))
        {
            var trimmed = line.Trim();
            if (trimmed.StartsWith("- [x] ", StringComparison.OrdinalIgnoreCase) &&
                trimmed[6..].TrimStart().StartsWith(optionPrefix, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Every attachment link in a field: markdown links and images to any https URL (GitHub
    /// attachments, or the presigned storage links the Worker writes), and bare GitHub
    /// attachment URLs.
    /// </summary>
    public IReadOnlyList<Attachment> Attachments(string fieldLabel)
    {
        var text = Get(fieldLabel);
        var result = new List<Attachment>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (Match match in MarkdownLink().Matches(text))
        {
            var url = match.Groups["url"].Value.Trim();
            if (seen.Add(url))
            {
                result.Add(new Attachment(match.Groups["name"].Value.Trim(), url, match.Value.StartsWith('!')));
            }
        }

        foreach (Match match in BareAttachmentUrl().Matches(text))
        {
            var url = match.Value.Trim();
            if (seen.Add(url))
            {
                result.Add(new Attachment(Path.GetFileName(new Uri(url).AbsolutePath), url, url.Contains("/assets/")));
            }
        }

        return result;
    }

    [GeneratedRegex(@"!?\[(?<name>[^\]]*)\]\((?<url>https?://[^)\s]+)\)")]
    private static partial Regex MarkdownLink();

    [GeneratedRegex(@"https://github\.com/(?:user-attachments/(?:files|assets)/|[^/\s]+/[^/\s]+/(?:files|assets)/)[^\s)>\]]+")]
    private static partial Regex BareAttachmentUrl();
}

public sealed record Attachment(string Name, string Url, bool IsImageLink);
