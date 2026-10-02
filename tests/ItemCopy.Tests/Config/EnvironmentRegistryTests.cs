using System.Collections;
using ItemCopy.Config;

namespace ItemCopy.Tests.Config;

public class EnvironmentRegistryTests
{
    private static EnvironmentRegistry Registry(params (string Key, string Value)[] values) =>
        new(values.ToDictionary(v => v.Key, v => v.Value));

    private static (string, string)[] Env(string name, string host = "x.sitecorecloud.io") =>
    [
        ($"SITECORE_{name}_HOST", host),
        ($"SITECORE_{name}_CLIENT_ID", "id"),
        ($"SITECORE_{name}_CLIENT_SECRET", "secret"),
    ];

    [Fact]
    public void Resolves_environment_case_insensitively()
    {
        var registry = Registry(Env("SIT", "https://sit.sitecorecloud.io/"));

        var env = registry.Resolve("sit");

        Assert.Equal("SIT", env.Name);
        Assert.Equal("sit.sitecorecloud.io", env.Host);
        Assert.Equal(new Uri("https://sit.sitecorecloud.io"), env.BaseUri);
        Assert.Equal("id", env.ClientId);
    }

    [Fact]
    public void Reports_missing_settings()
    {
        var registry = Registry(("SITECORE_PROD_HOST", "p"), ("SITECORE_PROD_CLIENT_ID", "  "));

        var missing = registry.MissingSettings("prod");

        Assert.Equal(["SITECORE_PROD_CLIENT_ID", "SITECORE_PROD_CLIENT_SECRET"], missing);
        var ex = Assert.Throws<ConfigException>(() => registry.Resolve("prod"));
        Assert.Contains("SITECORE_PROD_CLIENT_SECRET", ex.Message);
    }

    [Fact]
    public void Lists_only_fully_configured_environments()
    {
        var registry = Registry([.. Env("DEV"), .. Env("SIT"), ("SITECORE_PROD_HOST", "p")]);

        Assert.Equal(["DEV", "SIT"], registry.ConfiguredEnvironments);
    }

    [Fact]
    public void Protected_environments_default_to_prod()
    {
        Assert.True(Registry().IsProtected("prod"));
        Assert.False(Registry().IsProtected("sit"));
    }

    [Fact]
    public void Protected_environments_can_be_customised_or_disabled()
    {
        var custom = Registry(("SITECORE_PROTECTED_ENVS", "prod, uat"));
        Assert.True(custom.IsProtected("UAT"));
        Assert.True(custom.IsProtected("Prod"));

        var disabled = Registry(("SITECORE_PROTECTED_ENVS", ""));
        Assert.False(disabled.IsProtected("PROD"));
    }

    [Fact]
    public void Auth_settings_have_defaults()
    {
        Assert.Equal(EnvironmentRegistry.DefaultAuthUrl, Registry().AuthUrl);
        Assert.Equal(EnvironmentRegistry.DefaultAudience, Registry().AuthAudience);
        Assert.Equal("https://a/token", Registry(("SITECORE_AUTH_URL", "https://a/token")).AuthUrl);
    }

    [Fact]
    public void Rejects_invalid_names()
    {
        Assert.Throws<ConfigException>(() => Registry().MissingSettings("dev-1"));
    }

    [Fact]
    public void Load_uses_first_existing_file_and_process_env_overrides()
    {
        var dir = Directory.CreateTempSubdirectory();
        try
        {
            var envPath = Path.Combine(dir.FullName, ".env");
            File.WriteAllText(envPath, "SITECORE_DEV_HOST=file-host\nSITECORE_DEV_CLIENT_ID=file-id\n");
            var process = new Hashtable { ["SITECORE_DEV_CLIENT_ID"] = "process-id", ["PATH"] = "/bin" };

            var registry = EnvironmentRegistry.Load(null, [Path.Combine(dir.FullName, "missing.env"), envPath], process);

            Assert.Equal(envPath, registry.EnvFilePath);
            Assert.Equal(["SITECORE_DEV_CLIENT_SECRET"], registry.MissingSettings("dev"));
            var withSecret = EnvironmentRegistry.Load(envPath, [], new Hashtable(process) { ["SITECORE_DEV_CLIENT_SECRET"] = "s" });
            Assert.Equal("process-id", withSecret.Resolve("dev").ClientId);
            Assert.Equal("file-host", withSecret.Resolve("dev").Host);
        }
        finally
        {
            dir.Delete(recursive: true);
        }
    }

    [Fact]
    public void Load_fails_when_explicit_file_is_missing()
    {
        Assert.Throws<ConfigException>(() => EnvironmentRegistry.Load("/nope/.env", [], new Hashtable()));
    }

    [Fact]
    public void Load_works_without_any_file()
    {
        var registry = EnvironmentRegistry.Load(null, ["/nope/.env"], new Hashtable());
        Assert.Null(registry.EnvFilePath);
        Assert.Empty(registry.ConfiguredEnvironments);
    }
}
