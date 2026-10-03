namespace Workshop.Validator.Cli;

/// <summary>`command --name value --flag` parsing; repeated options keep the last value.</summary>
public sealed class Args
{
    private readonly Dictionary<string, string> _options = new(StringComparer.OrdinalIgnoreCase);

    public string? Command { get; private init; }

    public IReadOnlyList<string> Positional { get; private init; } = Array.Empty<string>();

    public static Args Parse(string[] args)
    {
        var positional = new List<string>();
        string? command = null;
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        for (var i = 0; i < args.Length; i++)
        {
            var arg = args[i];
            if (arg.StartsWith("--", StringComparison.Ordinal))
            {
                var name = arg[2..];
                if (i + 1 < args.Length && !args[i + 1].StartsWith("--", StringComparison.Ordinal))
                {
                    result[name] = args[++i];
                }
                else
                {
                    result[name] = "true";
                }
            }
            else if (command is null)
            {
                command = arg;
            }
            else
            {
                positional.Add(arg);
            }
        }

        var parsed = new Args { Command = command, Positional = positional };
        foreach (var pair in result)
        {
            parsed._options[pair.Key] = pair.Value;
        }

        return parsed;
    }

    public string? Get(string name) => _options.TryGetValue(name, out var value) ? value : null;

    public string Require(string name) =>
        Get(name) ?? throw new ValidationException($"Missing required option --{name}.");

    public bool Has(string name) => _options.ContainsKey(name);
}

/// <summary>A user-facing failure: printed without a stack trace, exit code 1.</summary>
public sealed class ValidationException(string message) : Exception(message);
