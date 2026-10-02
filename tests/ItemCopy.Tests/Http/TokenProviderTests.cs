using ItemCopy.Config;
using ItemCopy.Http;
using ItemCopy.Tests.Fakes;

namespace ItemCopy.Tests.Http;

public class TokenProviderTests
{
    private readonly FakeSitecore _fake = new();
    private readonly ManualTime _time = new();

    private TokenProvider Provider() => new(new HttpClient(_fake), FakeSitecore.AuthUrl, "https://api.test", _time);

    [Fact]
    public async Task Caches_tokens_until_near_expiry()
    {
        var provider = Provider();
        var env = new SitecoreEnvironment("DEV", "d", "dev-client", "secret");

        var first = await provider.GetTokenAsync(env, default);
        Assert.Equal(first, await provider.GetTokenAsync(env, default));

        _time.Advance(TimeSpan.FromHours(23) + TimeSpan.FromMinutes(56));
        var renewed = await provider.GetTokenAsync(env, default);

        Assert.NotEqual(first, renewed);
        Assert.Equal(2, _fake.TokensIssued["dev-client"]);
    }

    [Fact]
    public async Task Invalidate_forces_new_token()
    {
        var provider = Provider();
        var env = new SitecoreEnvironment("DEV", "d", "dev-client", "secret");

        var first = await provider.GetTokenAsync(env, default);
        provider.Invalidate(env, first);

        Assert.NotEqual(first, await provider.GetTokenAsync(env, default));
    }

    [Fact]
    public async Task Bad_credentials_give_a_clear_error_without_the_secret()
    {
        var env = new SitecoreEnvironment("PROD", "p", "prod-client", "wrong-secret");

        var ex = await Assert.ThrowsAsync<AuthException>(() => Provider().GetTokenAsync(env, default));

        Assert.Contains("PROD", ex.Message);
        Assert.Contains("access_denied", ex.Message);
        Assert.Contains("SITECORE_PROD_CLIENT_SECRET", ex.Message);
        Assert.DoesNotContain("wrong-secret", ex.Message);
    }
}
