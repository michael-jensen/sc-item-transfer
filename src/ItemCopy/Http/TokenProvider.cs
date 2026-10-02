using System.Text.Json;
using ItemCopy.Config;

namespace ItemCopy.Http;

/// <summary>
/// Gets client-credentials JWTs from Sitecore Cloud auth, one per environment, cached in memory
/// until shortly before they expire.
/// </summary>
public sealed class TokenProvider(HttpClient http, string authUrl, string audience, TimeProvider time, RetryPolicy? retryPolicy = null)
{
    private readonly RetryPolicy _retry = retryPolicy ?? new RetryPolicy();

    private static readonly TimeSpan ExpiryMargin = TimeSpan.FromMinutes(5);
    private static readonly TimeSpan DefaultLifetime = TimeSpan.FromHours(24);

    private readonly Dictionary<string, (string Token, DateTimeOffset ExpiresAt)> _cache = new(StringComparer.OrdinalIgnoreCase);
    private readonly SemaphoreSlim _lock = new(1, 1);

    public async Task<string> GetTokenAsync(SitecoreEnvironment env, CancellationToken ct)
    {
        await _lock.WaitAsync(ct);
        try
        {
            if (_cache.TryGetValue(env.Name, out var cached) && time.GetUtcNow() < cached.ExpiresAt)
                return cached.Token;

            var (token, lifetime) = await RequestTokenAsync(env, ct);
            _cache[env.Name] = (token, time.GetUtcNow() + lifetime - ExpiryMargin);
            return token;
        }
        finally
        {
            _lock.Release();
        }
    }

    /// <summary>Drops <paramref name="token"/> from the cache if it's still the current one, so the next call fetches a fresh token.</summary>
    public void Invalidate(SitecoreEnvironment env, string token)
    {
        _lock.Wait();
        try
        {
            if (_cache.TryGetValue(env.Name, out var cached) && cached.Token == token)
                _cache.Remove(env.Name);
        }
        finally
        {
            _lock.Release();
        }
    }

    private async Task<(string Token, TimeSpan Lifetime)> RequestTokenAsync(SitecoreEnvironment env, CancellationToken ct)
    {
        for (var attempt = 1; ; attempt++)
        {
            var canRetry = attempt <= _retry.MaxRetries;
            HttpResponseMessage response;
            try
            {
                using var request = BuildRequest(env);
                response = await http.SendAsync(request, ct);
            }
            catch (Exception ex) when (ex is HttpRequestException or IOException || ex is TaskCanceledException && !ct.IsCancellationRequested)
            {
                if (canRetry)
                {
                    await _retry.Delay(_retry.Backoff(attempt), ct);
                    continue;
                }
                var reason = ex is TaskCanceledException ? "the request timed out" : ex.Message;
                throw new AuthException($"Could not reach {authUrl} to get a token for {env.Name}: {reason}");
            }

            if (SitecoreHttp.IsTransientStatus(response.StatusCode) && canRetry)
            {
                response.Dispose();
                await _retry.Delay(_retry.Backoff(attempt), ct);
                continue;
            }

            return await ReadTokenAsync(env, response, ct);
        }
    }

    private HttpRequestMessage BuildRequest(SitecoreEnvironment env) =>
        new(HttpMethod.Post, authUrl)
        {
            Content = new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["client_id"] = env.ClientId,
                ["client_secret"] = env.ClientSecret,
                ["grant_type"] = "client_credentials",
                ["audience"] = audience,
            }),
        };

    private static async Task<(string Token, TimeSpan Lifetime)> ReadTokenAsync(SitecoreEnvironment env, HttpResponseMessage response, CancellationToken ct)
    {
        using (response)
        {
            var body = await response.Content.ReadAsStringAsync(ct);
            if (!response.IsSuccessStatusCode)
            {
                throw new AuthException(
                    $"Could not get a token for {env.Name}: HTTP {(int)response.StatusCode} {response.ReasonPhrase}{DescribeError(body)}. Check SITECORE_{env.Name}_CLIENT_ID and SITECORE_{env.Name}_CLIENT_SECRET.");
            }

            try
            {
                using var doc = JsonDocument.Parse(body);
                var root = doc.RootElement;
                var token = root.TryGetProperty("access_token", out var t) ? t.GetString() : null;
                if (string.IsNullOrEmpty(token))
                    throw new AuthException($"Token response for {env.Name} did not contain access_token.");

                var lifetime = root.TryGetProperty("expires_in", out var e) && e.TryGetInt32(out var seconds) && seconds > 0
                    ? TimeSpan.FromSeconds(seconds)
                    : DefaultLifetime;
                return (token, lifetime);
            }
            catch (JsonException)
            {
                throw new AuthException($"Token response for {env.Name} was not valid JSON.");
            }
        }
    }

    private static string DescribeError(string body)
    {
        try
        {
            using var doc = JsonDocument.Parse(body);
            var root = doc.RootElement;
            var error = root.TryGetProperty("error", out var e) ? e.GetString() : null;
            var description = root.TryGetProperty("error_description", out var d) ? d.GetString() : null;
            var parts = new[] { error, description }.Where(p => !string.IsNullOrEmpty(p));
            var text = string.Join(": ", parts);
            return text.Length > 0 ? $" ({text})" : "";
        }
        catch (JsonException)
        {
            return "";
        }
    }
}
