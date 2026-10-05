namespace ItemCopy.Config;

public sealed record JobValidationResult(Job? Job, IReadOnlyList<string> Errors, IReadOnlyList<string> Warnings)
{
    public bool IsValid => Errors.Count == 0;
}

/// <summary>Checks a job file against the configured environments and the ordering rules in the spec.</summary>
public static class JobValidator
{
    public const string DefaultDatabase = "master";

    public static JobValidationResult Validate(JobFile file, EnvironmentRegistry environments)
    {
        var errors = new List<string>();
        var warnings = new List<string>();

        var source = ValidateEnvironment("source", file.Source, environments, errors);
        var destination = ValidateEnvironment("destination", file.Destination, environments, errors);
        if (source is not null && destination is not null && source == destination)
            errors.Add($"\"source\" and \"destination\" are both '{source}'.");

        var database = file.Database is null ? DefaultDatabase : file.Database.Trim();
        if (database.Length == 0)
            errors.Add("\"database\" is empty. Leave it out to use master.");

        var items = ValidateItems(file.Items, errors);
        CheckOverlaps(items, errors, warnings);

        var job = errors.Count == 0 ? new Job(source!, destination!, database, items) : null;
        return new JobValidationResult(job, errors, warnings);
    }

    private static string? ValidateEnvironment(string field, string? name, EnvironmentRegistry environments, List<string> errors)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            errors.Add($"\"{field}\" is required.");
            return null;
        }

        name = name.Trim();
        if (!EnvironmentRegistry.IsValidName(name))
        {
            errors.Add($"\"{field}\" '{name}' is not a valid environment name. Use letters, digits and underscores only.");
            return null;
        }

        var missing = environments.MissingSettings(name);
        if (missing.Count > 0)
        {
            var configured = environments.ConfiguredEnvironments;
            var hint = configured.Count > 0 ? $" Configured environments: {string.Join(", ", configured)}." : "";
            errors.Add($"\"{field}\" environment '{name}' is not configured. Missing in .env: {string.Join(", ", missing)}.{hint}");
            return null;
        }

        return name.ToUpperInvariant();
    }

    private static List<JobItem> ValidateItems(List<JobFileItem?>? fileItems, List<string> errors)
    {
        var items = new List<JobItem>();
        if (fileItems is null || fileItems.Count == 0)
        {
            errors.Add("\"items\" must contain at least one item.");
            return items;
        }

        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        for (var i = 0; i < fileItems.Count; i++)
        {
            var label = $"items[{i}]";
            var fileItem = fileItems[i];
            if (fileItem is null)
            {
                errors.Add($"{label} is null.");
                continue;
            }

            var path = fileItem.Path?.Trim();
            if (string.IsNullOrEmpty(path))
            {
                errors.Add($"{label}: \"path\" is required.");
                continue;
            }
            if (!path.StartsWith("/sitecore/", StringComparison.OrdinalIgnoreCase))
            {
                errors.Add($"{label}: path '{path}' must start with /sitecore/.");
                continue;
            }
            path = path.TrimEnd('/');
            if (!seen.Add(path))
            {
                errors.Add($"{label}: path '{path}' is listed more than once.");
                continue;
            }

            var scope = ParseEnum(fileItem.Scope, TransferScope.SingleItem, $"{label}: \"scope\"", errors);
            var merge = ParseEnum(fileItem.MergeStrategy, MergeStrategy.OverrideExistingItem, $"{label}: \"mergeStrategy\"", errors);
            if (scope is not null && merge is not null)
                items.Add(new JobItem(path, scope.Value, merge.Value));
        }

        return items;
    }

    private static T? ParseEnum<T>(string? value, T defaultValue, string label, List<string> errors) where T : struct, Enum
    {
        if (string.IsNullOrWhiteSpace(value))
            return defaultValue;

        // Enum.TryParse also accepts numbers; only names are meaningful to the API.
        var match = Enum.GetNames<T>().FirstOrDefault(n => n.Equals(value.Trim(), StringComparison.OrdinalIgnoreCase));
        if (match is not null)
            return Enum.Parse<T>(match);

        errors.Add($"{label} '{value}' is not valid. Use one of: {string.Join(", ", Enum.GetNames<T>())}.");
        return null;
    }

    private static void CheckOverlaps(List<JobItem> items, List<string> errors, List<string> warnings)
    {
        for (var i = 0; i < items.Count; i++)
        {
            var earlier = items[i];
            if (earlier is { MergeStrategy: MergeStrategy.OverrideExistingTree, Scope: TransferScope.SingleItem })
            {
                warnings.Add($"'{earlier.Path}' uses OverrideExistingTree with SingleItem: all of its existing descendants in the destination will be deleted and only the item itself copied.");
            }

            for (var j = i + 1; j < items.Count; j++)
            {
                var later = items[j];

                if (IsAncestor(later.Path, earlier.Path))
                {
                    // A descendant is listed before its ancestor.
                    if (later.MergeStrategy == MergeStrategy.OverrideExistingTree)
                        errors.Add($"'{earlier.Path}' is listed before its ancestor '{later.Path}', which uses OverrideExistingTree and would delete it. Move '{earlier.Path}' after '{later.Path}'.");
                    else
                        warnings.Add($"'{earlier.Path}' is listed before its ancestor '{later.Path}'. If the ancestor doesn't exist in the destination yet, '{earlier.Path}' won't appear in the content tree.");
                }
                else if (IsAncestor(earlier.Path, later.Path) && earlier.Scope == TransferScope.ItemAndDescendants)
                {
                    warnings.Add($"'{later.Path}' is already included by '{earlier.Path}' (ItemAndDescendants); it will be transferred again with its own merge strategy.");
                }
            }
        }
    }

    /// <summary>True if <paramref name="ancestor"/> is a strict ancestor of <paramref name="path"/>.</summary>
    internal static bool IsAncestor(string ancestor, string path) =>
        path.Length > ancestor.Length + 1
        && path.StartsWith(ancestor, StringComparison.OrdinalIgnoreCase)
        && path[ancestor.Length] == '/';
}
