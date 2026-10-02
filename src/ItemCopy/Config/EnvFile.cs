namespace ItemCopy.Config;

/// <summary>
/// Minimal .env parser: <c>KEY=VALUE</c> lines, <c>#</c> comment lines, optional <c>export </c>
/// prefix, optional matching single or double quotes around the value. No interpolation and no
/// inline comments, so secrets containing <c>#</c> are safe.
/// </summary>
public static class EnvFile
{
    public static Dictionary<string, string> Parse(string content)
    {
        var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        foreach (var rawLine in content.Split('\n'))
        {
            var line = rawLine.Trim();
            if (line.Length == 0 || line.StartsWith('#'))
                continue;

            if (line.StartsWith("export ", StringComparison.Ordinal))
                line = line["export ".Length..].TrimStart();

            var eq = line.IndexOf('=');
            if (eq <= 0)
                continue;

            var key = line[..eq].Trim();
            var value = line[(eq + 1)..].Trim();

            if (value.Length >= 2 && (value[0] == '"' || value[0] == '\'') && value[^1] == value[0])
                value = value[1..^1];

            values[key] = value;
        }

        return values;
    }
}
