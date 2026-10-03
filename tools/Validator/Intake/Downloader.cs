using System.Net.Http.Headers;

namespace Workshop.Validator.Intake;

/// <summary>Fetches submission files to disk with a size cap; follows redirects.</summary>
public sealed class Downloader : IDisposable
{
    public const long MaxBytes = 2L * 1024 * 1024 * 1024;

    private readonly HttpClient _client;

    public Downloader(string? githubToken)
    {
        _client = new HttpClient(new HttpClientHandler { AllowAutoRedirect = true, MaxAutomaticRedirections = 10 })
        {
            Timeout = TimeSpan.FromMinutes(30)
        };
        _client.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("PlayniteAchievements-Workshop", "1.0"));
        if (!string.IsNullOrWhiteSpace(githubToken))
        {
            _githubToken = githubToken;
        }
    }

    private readonly string? _githubToken;

    /// <summary>Downloads to <paramref name="destination"/>; returns the response content type, if any.</summary>
    public async Task<string?> DownloadAsync(string url, string destination)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        // Attachments on github.com are public for a public repository; the token only helps
        // when the repository (and so its attachments) is private.
        if (_githubToken is not null && url.StartsWith("https://github.com/", StringComparison.OrdinalIgnoreCase))
        {
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _githubToken);
        }

        using var response = await _client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead);
        if (!response.IsSuccessStatusCode)
        {
            throw new Cli.ValidationException($"Downloading '{url}' failed with HTTP {(int)response.StatusCode}.");
        }

        if (response.Content.Headers.ContentLength is > MaxBytes)
        {
            throw new Cli.ValidationException($"'{url}' is larger than {MaxBytes} bytes.");
        }

        Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
        await using var source = await response.Content.ReadAsStreamAsync();
        await using var target = File.Create(destination);
        var buffer = new byte[1 << 16];
        long total = 0;
        int read;
        while ((read = await source.ReadAsync(buffer)) > 0)
        {
            total += read;
            if (total > MaxBytes)
            {
                throw new Cli.ValidationException($"'{url}' is larger than {MaxBytes} bytes.");
            }

            await target.WriteAsync(buffer.AsMemory(0, read));
        }

        return response.Content.Headers.ContentType?.MediaType;
    }

    public void Dispose() => _client.Dispose();
}
