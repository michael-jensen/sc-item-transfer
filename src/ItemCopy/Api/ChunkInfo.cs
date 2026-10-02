using ItemCopy.Http;

namespace ItemCopy.Api;

/// <summary>Metadata the source returns in the <c>Content-Disposition</c> header of a chunk response.</summary>
public sealed record ChunkInfo(bool IsMedia, int? ItemsProcessed, int? ItemsSkipped)
{
    /// <summary>
    /// Parses e.g. <c>attachment; filename=chunk0; IsMedia=true; ItemsProcessed=10; ItemsSkipped=0</c>.
    /// Parameter names are case-insensitive and values may be quoted. <c>IsMedia</c> is required
    /// because the destination needs it to decode the chunk; it is never guessed.
    /// </summary>
    public static ChunkInfo Parse(string? contentDisposition)
    {
        var parameters = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var part in (contentDisposition ?? "").Split(';', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
        {
            var eq = part.IndexOf('=');
            if (eq > 0)
                parameters[part[..eq].Trim()] = part[(eq + 1)..].Trim().Trim('"');
        }

        if (!parameters.TryGetValue("IsMedia", out var isMediaText) || !bool.TryParse(isMediaText, out var isMedia))
            throw new SitecoreApiException($"Chunk response has no valid IsMedia value in Content-Disposition ('{contentDisposition}').");

        return new ChunkInfo(isMedia, ParseInt(parameters, "ItemsProcessed"), ParseInt(parameters, "ItemsSkipped"));
    }

    private static int? ParseInt(Dictionary<string, string> parameters, string name) =>
        parameters.TryGetValue(name, out var text) && int.TryParse(text, out var value) ? value : null;
}
