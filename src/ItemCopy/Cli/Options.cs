using System.Globalization;
using System.Text.RegularExpressions;
using ItemCopy.Config;

namespace ItemCopy.Cli;

public enum Command
{
    Run,
    Help,
    Version,
}

public sealed record Options(
    Command Command,
    string? JobPath = null,
    bool Yes = false,
    string? ConfirmEnv = null,
    bool DryRun = false,
    string? EnvFile = null,
    TimeSpan? Timeout = null,
    bool Verbose = false)
{
    public static readonly TimeSpan DefaultTimeout = TimeSpan.FromMinutes(30);

    public const string Usage = """
        item-copy — copy Sitecore content between SitecoreAI environments.

        Usage:
          item-copy run <job.json> [options]
          item-copy --help | --version

        Options:
          --yes                 Don't ask for confirmation (protected environments also need --confirm-env).
          --confirm-env <name>  Confirm a protected destination (e.g. PROD) without typing it.
          --dry-run             Validate the job and test credentials for both environments, then stop.
          --env-file <path>     Use this .env file instead of ./.env or the one next to the executable.
          --timeout <duration>  Maximum time for any single wait, e.g. 90s, 30m, 2h. Default 30m.
          --verbose             Log every HTTP request.

        Job files: see jobs/job.example.json. Environments: see .env.example.
        """;

    public static Options Parse(IReadOnlyList<string> args)
    {
        if (args.Count == 0 || args[0] is "-h" or "--help" or "help")
            return new Options(Command.Help);
        if (args[0] is "--version" or "-v")
            return new Options(Command.Version);
        if (args[0] != "run")
            throw new ConfigException($"Unknown command '{args[0]}'. Run 'item-copy --help' for usage.");

        string? jobPath = null, confirmEnv = null, envFile = null;
        bool yes = false, dryRun = false, verbose = false;
        TimeSpan? timeout = null;

        for (var i = 1; i < args.Count; i++)
        {
            var arg = args[i];
            switch (arg)
            {
                case "--yes" or "-y":
                    yes = true;
                    break;
                case "--dry-run":
                    dryRun = true;
                    break;
                case "--verbose":
                    verbose = true;
                    break;
                case "--confirm-env":
                    confirmEnv = Value(args, ref i, arg);
                    break;
                case "--env-file":
                    envFile = Value(args, ref i, arg);
                    break;
                case "--timeout":
                    timeout = ParseDuration(Value(args, ref i, arg));
                    break;
                case "-h" or "--help":
                    return new Options(Command.Help);
                default:
                    if (arg.StartsWith('-'))
                        throw new ConfigException($"Unknown option '{arg}'. Run 'item-copy --help' for usage.");
                    if (jobPath is not null)
                        throw new ConfigException($"Only one job file can be given (got '{jobPath}' and '{arg}').");
                    jobPath = arg;
                    break;
            }
        }

        if (jobPath is null)
            throw new ConfigException("Missing job file. Usage: item-copy run <job.json>");

        return new Options(Command.Run, jobPath, yes, confirmEnv, dryRun, envFile, timeout, verbose);
    }

    /// <summary>Parses <c>90s</c>, <c>30m</c>, <c>2h</c>, <c>1.5h</c>.</summary>
    public static TimeSpan ParseDuration(string text)
    {
        var match = Regex.Match(text.Trim(), @"^(\d+(?:\.\d+)?)(s|m|h)$", RegexOptions.IgnoreCase);
        if (!match.Success)
            throw new ConfigException($"Invalid duration '{text}'. Use a number followed by s, m or h, e.g. 30m.");

        var value = double.Parse(match.Groups[1].Value, CultureInfo.InvariantCulture);
        var duration = match.Groups[2].Value.ToLowerInvariant() switch
        {
            "s" => TimeSpan.FromSeconds(value),
            "m" => TimeSpan.FromMinutes(value),
            _ => TimeSpan.FromHours(value),
        };
        return duration > TimeSpan.Zero ? duration : throw new ConfigException("Duration must be greater than zero.");
    }

    private static string Value(IReadOnlyList<string> args, ref int i, string option)
    {
        if (i + 1 >= args.Count || args[i + 1].StartsWith("--"))
            throw new ConfigException($"{option} needs a value.");
        return args[++i];
    }
}
