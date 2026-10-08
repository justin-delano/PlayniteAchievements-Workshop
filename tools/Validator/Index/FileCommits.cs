using System.Diagnostics;

namespace Workshop.Validator.Index;

/// <summary>
/// The last commit that changed each file in the repository, read from one `git log` pass. The
/// index pins each item file's CDN URL to that commit, so the URL changes only when the file does
/// and both the CDN and the extension's image cache stay warm across index rebuilds.
/// </summary>
public sealed class FileCommits
{
    private readonly Dictionary<string, string> _byPath = new(StringComparer.Ordinal);

    /// <summary>Reads the history under root; an empty map when git is unavailable or root is not a repository.</summary>
    public static FileCommits Load(string root)
    {
        var commits = new FileCommits();
        try
        {
            // %x00 marks each commit line; paths follow it, newest commit first, so the first
            // commit seen for a path is its last change. quotepath=off keeps non-ASCII names raw.
            var psi = new ProcessStartInfo("git", "-c core.quotepath=off log --format=%x00%H --name-only --no-renames --relative")
            {
                WorkingDirectory = root,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false
            };
            using var process = Process.Start(psi)!;
            var stderr = process.StandardError.ReadToEndAsync();
            string? current = null;
            string? line;
            while ((line = process.StandardOutput.ReadLine()) is not null)
            {
                if (line.Length == 0)
                {
                    continue;
                }

                if (line[0] == '\0')
                {
                    current = line.Substring(1);
                }
                else if (current is not null)
                {
                    commits._byPath.TryAdd(line, current);
                }
            }

            process.WaitForExit();
            _ = stderr.Result;
            if (process.ExitCode != 0)
            {
                commits._byPath.Clear();
            }
        }
        catch (Exception)
        {
            commits._byPath.Clear();
        }

        return commits;
    }

    /// <summary>The last commit that changed path (relative to root, forward slashes), or fallback when it has no history.</summary>
    public string For(string path, string fallback) =>
        _byPath.TryGetValue(path, out var commit) ? commit : fallback;
}
