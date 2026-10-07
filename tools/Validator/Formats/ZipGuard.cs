using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Workshop.Validator.Formats;

/// <summary>
/// Zip reading that trusts nothing in the archive: normalized entry names, traversal checks,
/// entry caps, bounded reads, and content sniffing by magic bytes. Mirrors the extension's
/// PortablePackage helper so a file the validator accepts is one the extension can read.
/// </summary>
public static class ZipGuard
{
    public const int MaxEntryCount = 50_000;
    public const long MaxEntryBytes = 256L * 1024 * 1024;
    public const long MaxTextEntryBytes = 16L * 1024 * 1024;
    public const long MaxPreviewBytes = 5L * 1024 * 1024;

    public static bool IsZip(string path)
    {
        using var stream = File.OpenRead(path);
        return stream.ReadByte() == 0x50 && stream.ReadByte() == 0x4B;
    }

    public static string? NormalizeEntryName(string? value)
    {
        var normalized = (value ?? string.Empty).Trim().Replace('\\', '/').TrimStart('/');
        return normalized.Length == 0 ? null : normalized;
    }

    public static bool IsFlatEntryUnder(string? normalizedEntryName, string folder)
    {
        if (string.IsNullOrWhiteSpace(normalizedEntryName))
        {
            return false;
        }

        var prefix = folder.TrimEnd('/') + "/";
        if (!normalizedEntryName.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var fileName = normalizedEntryName[prefix.Length..];
        return fileName.Length > 0 &&
               !fileName.Contains('/') &&
               !fileName.Contains("..") &&
               fileName.IndexOfAny(Path.GetInvalidFileNameChars()) < 0;
    }

    /// <summary>Every file entry by normalized name (first wins on a clash), after the caps.</summary>
    public static IReadOnlyDictionary<string, ZipArchiveEntry> Index(ZipArchive archive, List<string> errors)
    {
        var result = new Dictionary<string, ZipArchiveEntry>(StringComparer.OrdinalIgnoreCase);
        if (archive.Entries.Count > MaxEntryCount)
        {
            errors.Add($"The package has {archive.Entries.Count} entries; the limit is {MaxEntryCount}.");
            return result;
        }

        foreach (var entry in archive.Entries)
        {
            var name = NormalizeEntryName(entry.FullName);
            if (name is null || entry.Name.Length == 0)
            {
                continue;
            }

            if (name.Contains("..") || Path.IsPathRooted(name))
            {
                errors.Add($"The package entry '{entry.FullName}' has an unsafe path.");
                continue;
            }

            if (entry.Length > MaxEntryBytes)
            {
                errors.Add($"The package entry '{name}' is {entry.Length} bytes; the limit is {MaxEntryBytes}.");
                continue;
            }

            result.TryAdd(name, entry);
        }

        return result;
    }

    public static string ReadText(ZipArchiveEntry entry)
    {
        if (entry.Length > MaxTextEntryBytes)
        {
            throw new Cli.ValidationException($"The text entry '{entry.FullName}' is too large to read.");
        }

        using var stream = entry.Open();
        using var reader = new StreamReader(stream, new UTF8Encoding(false), detectEncodingFromByteOrderMarks: true);
        return reader.ReadToEnd();
    }

    public static JsonObject? ReadJsonObject(ZipArchiveEntry entry, List<string> errors)
    {
        try
        {
            return JsonNode.Parse(ReadText(entry)) as JsonObject;
        }
        catch (JsonException ex)
        {
            errors.Add($"The entry '{entry.FullName}' is not valid JSON: {ex.Message}");
            return null;
        }
    }

    public static byte[] ReadHead(ZipArchiveEntry entry, int count = 16)
    {
        using var stream = entry.Open();
        var buffer = new byte[count];
        var total = 0;
        int read;
        while (total < count && (read = stream.Read(buffer, total, count - total)) > 0)
        {
            total += read;
        }

        return buffer[..total];
    }

    public static byte[] ReadHead(string path, int count = 16)
    {
        using var stream = File.OpenRead(path);
        var buffer = new byte[count];
        var total = stream.Read(buffer, 0, count);
        return buffer[..total];
    }

    public static string ExtractToTemp(ZipArchiveEntry entry, string directory)
    {
        Directory.CreateDirectory(directory);
        var target = Path.Combine(directory, Guid.NewGuid().ToString("N") + Path.GetExtension(entry.Name));
        using var source = entry.Open();
        using var destination = File.Create(target);
        source.CopyTo(destination);
        return target;
    }

    public static string Sha256Hex(string path)
    {
        using var stream = File.OpenRead(path);
        return Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant();
    }

    // Image and audio sniffing, by the first bytes rather than the extension.

    public static string? ImageExtensionFromMagic(byte[] head)
    {
        if (head.Length >= 8 && head[0] == 0x89 && head[1] == 0x50 && head[2] == 0x4E && head[3] == 0x47)
        {
            return ".png";
        }

        if (head.Length >= 3 && head[0] == 0xFF && head[1] == 0xD8 && head[2] == 0xFF)
        {
            return ".jpg";
        }

        if (head.Length >= 4 && head[0] == (byte)'G' && head[1] == (byte)'I' && head[2] == (byte)'F' && head[3] == (byte)'8')
        {
            return ".gif";
        }

        if (head.Length >= 12 && head[0] == (byte)'R' && head[1] == (byte)'I' && head[2] == (byte)'F' && head[3] == (byte)'F' &&
            head[8] == (byte)'W' && head[9] == (byte)'E' && head[10] == (byte)'B' && head[11] == (byte)'P')
        {
            return ".webp";
        }

        return null;
    }

    public static bool IsImage(byte[] head) => ImageExtensionFromMagic(head) is not null;

    /// <summary>Bytes to read for <see cref="PackageImageExtensionFromMagic"/>: the WebM DocType sits past the first 16.</summary>
    public const int PackageImageHeadBytes = 64;

    /// <summary>A WebM file: an EBML header whose DocType is "webm".</summary>
    public static bool IsWebm(byte[] head)
    {
        if (head.Length < 4 || head[0] != 0x1A || head[1] != 0x45 || head[2] != 0xDF || head[3] != 0xA3)
        {
            return false;
        }

        return System.Text.Encoding.ASCII.GetString(head).Contains("webm", StringComparison.Ordinal);
    }

    /// <summary>
    /// Images a package may carry for the extension to show: the still formats plus animated WebM.
    /// Covers and previews stay still images, since GitHub pages and release notes show those.
    /// </summary>
    public static string? PackageImageExtensionFromMagic(byte[] head) =>
        ImageExtensionFromMagic(head) ?? (IsWebm(head) ? ".webm" : null);

    public static bool IsAudio(byte[] head, string extension)
    {
        switch (extension.ToLowerInvariant())
        {
            case ".wav":
                return head.Length >= 12 && head[0] == (byte)'R' && head[1] == (byte)'I' && head[2] == (byte)'F' && head[3] == (byte)'F' &&
                       head[8] == (byte)'W' && head[9] == (byte)'A' && head[10] == (byte)'V' && head[11] == (byte)'E';
            case ".flac":
                return head.Length >= 4 && head[0] == (byte)'f' && head[1] == (byte)'L' && head[2] == (byte)'a' && head[3] == (byte)'C';
            case ".mp3":
                return head.Length >= 3 &&
                       ((head[0] == (byte)'I' && head[1] == (byte)'D' && head[2] == (byte)'3') ||
                        (head[0] == 0xFF && (head[1] & 0xE0) == 0xE0));
            default:
                return false;
        }
    }

    public static readonly string[] AudioExtensions = { ".wav", ".mp3", ".flac" };
}
