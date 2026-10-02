using System.Collections;
using System.Text.RegularExpressions;

namespace ItemCopy.Config;

/// <summary>A SitecoreAI environment resolved from <c>SITECORE_{NAME}_*</c> settings.</summary>
public sealed record SitecoreEnvironment(string Name, string Host, string ClientId, string ClientSecret)
{
    public Uri BaseUri { get; } = new($"https://{Host}");

    public override string ToString() => $"{Name} ({Host})";
}

/// <summary>
/// Resolves named environments (DEV, SIT, PROD, ...) from merged .env and process environment
/// values. Nothing is hard-coded to particular environment names.
/// </summary>
public sealed partial class EnvironmentRegistry
{
    public const string Prefix = "SITECORE_";
    public const string DefaultAuthUrl = "https://auth.sitecorecloud.io/oauth/token";
    public const string DefaultAudience = "https://api.sitecorecloud.io";
    public const string DefaultProtectedEnvs = "PROD";

    private readonly IReadOnlyDictionary<string, string> _values;

    public EnvironmentRegistry(IReadOnlyDictionary<string, string> values, string? envFilePath = null)
    {
        _values = new Dictionary<string, string>(values, StringComparer.OrdinalIgnoreCase);
        EnvFilePath = envFilePath;
    }

    /// <summary>The .env file that was loaded, or null if values came only from the process environment.</summary>
    public string? EnvFilePath { get; }

    public string AuthUrl => GetNonEmpty("SITECORE_AUTH_URL") ?? DefaultAuthUrl;

    public string AuthAudience => GetNonEmpty("SITECORE_AUTH_AUDIENCE") ?? DefaultAudience;

    /// <summary>Upper-cased names of environments that need typed confirmation as a destination.</summary>
    public IReadOnlySet<string> ProtectedEnvironments
    {
        get
        {
            // Unset → default; set to empty → protection disabled.
            var raw = _values.TryGetValue("SITECORE_PROTECTED_ENVS", out var v) ? v : DefaultProtectedEnvs;
            return raw.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Select(n => n.ToUpperInvariant())
                .ToHashSet();
        }
    }

    public bool IsProtected(string name) => ProtectedEnvironments.Contains(name.ToUpperInvariant());

    /// <summary>Names of all environments that have every required setting.</summary>
    public IReadOnlyList<string> ConfiguredEnvironments =>
        _values.Keys
            .Select(k => HostKeyPattern().Match(k))
            .Where(m => m.Success)
            .Select(m => m.Groups[1].Value.ToUpperInvariant())
            .Where(n => MissingSettings(n).Count == 0)
            .Distinct()
            .Order(StringComparer.Ordinal)
            .ToList();

    /// <summary>Settings that must be added to .env for <paramref name="name"/> to be usable. Empty when configured.</summary>
    public IReadOnlyList<string> MissingSettings(string name)
    {
        if (!IsValidName(name))
            throw new ConfigException($"Invalid environment name '{name}'. Use letters, digits and underscores only.");

        var upper = name.ToUpperInvariant();
        return new[] { "HOST", "CLIENT_ID", "CLIENT_SECRET" }
            .Select(s => $"{Prefix}{upper}_{s}")
            .Where(key => GetNonEmpty(key) is null)
            .ToList();
    }

    public SitecoreEnvironment Resolve(string name)
    {
        var missing = MissingSettings(name);
        if (missing.Count > 0)
            throw new ConfigException($"Environment '{name}' is not configured. Missing: {string.Join(", ", missing)}");

        var upper = name.ToUpperInvariant();
        return new SitecoreEnvironment(
            upper,
            NormalizeHost(GetNonEmpty($"{Prefix}{upper}_HOST")!),
            GetNonEmpty($"{Prefix}{upper}_CLIENT_ID")!,
            GetNonEmpty($"{Prefix}{upper}_CLIENT_SECRET")!);
    }

    public static bool IsValidName(string name) => NamePattern().IsMatch(name);

    public static string NormalizeHost(string host)
    {
        var h = host.Trim();
        foreach (var scheme in new[] { "https://", "http://" })
        {
            if (h.StartsWith(scheme, StringComparison.OrdinalIgnoreCase))
                h = h[scheme.Length..];
        }
        return h.TrimEnd('/');
    }

    /// <summary>
    /// Loads settings: <paramref name="explicitEnvFile"/> if given (must exist), otherwise the first
    /// existing file in <paramref name="searchPaths"/>. Process environment variables starting with
    /// <c>SITECORE_</c> override file values.
    /// </summary>
    public static EnvironmentRegistry Load(string? explicitEnvFile, IEnumerable<string> searchPaths, IDictionary processEnvironment)
    {
        string? path = null;
        if (explicitEnvFile is not null)
        {
            if (!File.Exists(explicitEnvFile))
                throw new ConfigException($"Env file not found: {explicitEnvFile}");
            path = Path.GetFullPath(explicitEnvFile);
        }
        else
        {
            path = searchPaths.Select(Path.GetFullPath).FirstOrDefault(File.Exists);
        }

        var values = path is null
            ? new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            : EnvFile.Parse(File.ReadAllText(path));

        foreach (DictionaryEntry entry in processEnvironment)
        {
            if (entry.Key is string key && key.StartsWith(Prefix, StringComparison.OrdinalIgnoreCase) && entry.Value is string value)
                values[key] = value;
        }

        return new EnvironmentRegistry(values, path);
    }

    private string? GetNonEmpty(string key) =>
        _values.TryGetValue(key, out var v) && !string.IsNullOrWhiteSpace(v) ? v.Trim() : null;

    [GeneratedRegex("^[A-Za-z0-9_]+$")]
    private static partial Regex NamePattern();

    [GeneratedRegex("^SITECORE_([A-Za-z0-9_]+)_HOST$", RegexOptions.IgnoreCase)]
    private static partial Regex HostKeyPattern();
}
