using ItemCopy.Config;

namespace ItemCopy.Tests.Config;

public class JobValidatorTests
{
    private static readonly EnvironmentRegistry Environments = new(new Dictionary<string, string>
    {
        ["SITECORE_DEV_HOST"] = "dev", ["SITECORE_DEV_CLIENT_ID"] = "i", ["SITECORE_DEV_CLIENT_SECRET"] = "s",
        ["SITECORE_SIT_HOST"] = "sit", ["SITECORE_SIT_CLIENT_ID"] = "i", ["SITECORE_SIT_CLIENT_SECRET"] = "s",
    });

    private static JobValidationResult Validate(string json) => JobValidator.Validate(JobFile.Parse(json, "test.json"), Environments);

    private static string JobJson(string items, string source = "dev", string destination = "sit") =>
        $$"""{ "source": "{{source}}", "destination": "{{destination}}", "items": [ {{items}} ] }""";

    [Fact]
    public void Applies_defaults_and_normalises()
    {
        var result = Validate("""
            {
              // comments and trailing commas are allowed
              "$schema": "../job.schema.json",
              "Source": "dev",
              "destination": "Sit",
              "items": [
                { "path": " /sitecore/content/Home/ " },
                { "path": "/sitecore/media library/Images", "scope": "itemanddescendants", "mergeStrategy": "keepexistingitem" },
              ],
            }
            """);

        Assert.True(result.IsValid, string.Join("\n", result.Errors));
        var job = result.Job!;
        Assert.Equal("DEV", job.Source);
        Assert.Equal("SIT", job.Destination);
        Assert.Equal("master", job.Database);
        Assert.Equal(new JobItem("/sitecore/content/Home", TransferScope.SingleItem, MergeStrategy.OverrideExistingItem), job.Items[0]);
        Assert.Equal(new JobItem("/sitecore/media library/Images", TransferScope.ItemAndDescendants, MergeStrategy.KeepExistingItem), job.Items[1]);
        Assert.Empty(result.Warnings);
    }

    [Theory]
    [InlineData("""{ "destination": "sit", "items": [{ "path": "/sitecore/a" }] }""", "\"source\" is required")]
    [InlineData("""{ "source": "dev", "destination": "prod", "items": [{ "path": "/sitecore/a" }] }""", "'prod' is not configured")]
    [InlineData("""{ "source": "dev", "destination": "DEV", "items": [{ "path": "/sitecore/a" }] }""", "are both 'DEV'")]
    [InlineData("""{ "source": "dev", "destination": "sit", "items": [] }""", "at least one item")]
    [InlineData("""{ "source": "dev", "destination": "sit", "database": " ", "items": [{ "path": "/sitecore/a" }] }""", "\"database\" is empty")]
    [InlineData("""{ "source": "dev-1", "destination": "sit", "items": [{ "path": "/sitecore/a" }] }""", "not a valid environment name")]
    public void Reports_job_level_errors(string json, string expected)
    {
        var result = Validate(json);

        Assert.False(result.IsValid);
        Assert.Null(result.Job);
        Assert.Contains(result.Errors, e => e.Contains(expected));
    }

    [Theory]
    [InlineData("""{ "scope": "SingleItem" }""", "\"path\" is required")]
    [InlineData("""{ "path": "/content/Home" }""", "must start with /sitecore/")]
    [InlineData("""{ "path": "/sitecore/a", "scope": "Children" }""", "\"scope\" 'Children' is not valid")]
    [InlineData("""{ "path": "/sitecore/a", "scope": "1" }""", "\"scope\" '1' is not valid")]
    [InlineData("""{ "path": "/sitecore/a", "mergeStrategy": "Replace" }""", "\"mergeStrategy\" 'Replace' is not valid")]
    [InlineData("""{ "path": "/sitecore/a" }, { "path": "/Sitecore/A/" }""", "listed more than once")]
    public void Reports_item_errors(string items, string expected)
    {
        var result = Validate(JobJson(items));

        Assert.Contains(result.Errors, e => e.Contains(expected));
    }

    [Fact]
    public void Error_when_descendant_precedes_override_tree_ancestor()
    {
        var result = Validate(JobJson("""
            { "path": "/sitecore/content/Home/About" },
            { "path": "/sitecore/content/Home", "scope": "ItemAndDescendants", "mergeStrategy": "OverrideExistingTree" }
            """));

        Assert.Contains(result.Errors, e => e.Contains("would delete it"));
    }

    [Fact]
    public void Warns_when_descendant_precedes_ancestor()
    {
        var result = Validate(JobJson("""
            { "path": "/sitecore/content/Home/About" },
            { "path": "/sitecore/content/Home" }
            """));

        Assert.True(result.IsValid);
        Assert.Contains(result.Warnings, w => w.Contains("is listed before its ancestor"));
    }

    [Fact]
    public void Warns_when_path_is_already_covered_by_earlier_subtree()
    {
        var result = Validate(JobJson("""
            { "path": "/sitecore/content/Home", "scope": "ItemAndDescendants" },
            { "path": "/sitecore/content/Home/About", "mergeStrategy": "KeepExistingItem" }
            """));

        Assert.True(result.IsValid);
        Assert.Contains(result.Warnings, w => w.Contains("is already included by"));
    }

    [Fact]
    public void No_overlap_warning_for_siblings_with_shared_prefix()
    {
        var result = Validate(JobJson("""
            { "path": "/sitecore/content/Home", "scope": "ItemAndDescendants" },
            { "path": "/sitecore/content/HomePage" }
            """));

        Assert.Empty(result.Warnings);
    }

    [Fact]
    public void Warns_about_override_tree_with_single_item()
    {
        var result = Validate(JobJson("""{ "path": "/sitecore/content/Home", "mergeStrategy": "OverrideExistingTree" }"""));

        Assert.True(result.IsValid);
        Assert.Contains(result.Warnings, w => w.Contains("OverrideExistingTree with SingleItem"));
    }

    [Fact]
    public void Invalid_json_reports_line()
    {
        var ex = Assert.Throws<ConfigException>(() => JobFile.Parse("{\n  \"source\": \n}", "bad.json"));
        Assert.Contains("bad.json", ex.Message);
        Assert.Contains("line", ex.Message);
    }
}
