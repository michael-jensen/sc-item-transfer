using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using ItemCopy.Api;
using ItemCopy.Cli;
using ItemCopy.Config;

namespace ItemCopy.Http;

public sealed class RetryPolicy
{
    public int MaxRetries { get; init; } = 3;

    /// <summary>Delay before retry number <c>attempt</c> (1-based): 2s, 4s, 8s.</summary>
    public Func<int, TimeSpan> Backoff { get; init; } = attempt => TimeSpan.FromSeconds(2 * Math.Pow(2, attempt - 1));

    public Func<TimeSpan, CancellationToken, Task> Delay { get; init; } = Task.Delay;
}

/// <summary>
/// Sends authorised requests to a Sitecore environment: adds the bearer token, refreshes it once on
/// 401/403, retries transient failures, and logs requests in verbose mode.
/// </summary>
public sealed class SitecoreHttp(HttpClient http, TokenProvider tokens, RetryPolicy retry, Ui ui)
{
    public RetryPolicy Retry => retry;

    /// <param name="build">Creates a fresh request for each attempt.</param>
    /// <param name="replayable">
    /// When false (e.g. a PUT whose body is a one-shot stream) the request is sent exactly once; a
    /// rejected token is still invalidated so the caller's own retry gets a fresh one.
    /// </param>
    /// <param name="retryTransient">Retry 408/429/5xx and network errors. Only for idempotent requests.</param>
    public async Task<HttpResponseMessage> SendAsync(
        SitecoreEnvironment env,
        Func<HttpRequestMessage> build,
        CancellationToken ct,
        bool replayable = true,
        bool retryTransient = true,
        HttpCompletionOption completion = HttpCompletionOption.ResponseContentRead)
    {
        var refreshed = false;
        var transientAttempts = 0;

        while (true)
        {
            var token = await tokens.GetTokenAsync(env, ct);
            using var request = build();
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));

            var stopwatch = Stopwatch.StartNew();
            HttpResponseMessage response;
            try
            {
                response = await http.SendAsync(request, completion, ct);
            }
            catch (Exception ex) when (IsTransientException(ex, ct) && replayable && retryTransient && transientAttempts < retry.MaxRetries)
            {
                transientAttempts++;
                var delay = retry.Backoff(transientAttempts);
                ui.Warn($"{request.Method} {request.RequestUri?.AbsolutePath} failed ({ex.Message}); retrying in {delay.TotalSeconds:0}s ({transientAttempts}/{retry.MaxRetries}).");
                await retry.Delay(delay, ct);
                continue;
            }

            ui.Debug($"{request.Method} {request.RequestUri} → {(int)response.StatusCode} ({stopwatch.ElapsedMilliseconds} ms)");

            if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
            {
                tokens.Invalidate(env, token);
                if (replayable && !refreshed)
                {
                    refreshed = true;
                    ui.Debug($"{env.Name} rejected the token; requesting a new one.");
                    response.Dispose();
                    continue;
                }
                return response;
            }

            if (IsTransientStatus(response.StatusCode) && replayable && retryTransient && transientAttempts < retry.MaxRetries)
            {
                transientAttempts++;
                var delay = RetryAfter(response) ?? retry.Backoff(transientAttempts);
                ui.Warn($"{request.Method} {request.RequestUri?.AbsolutePath} returned {(int)response.StatusCode}; retrying in {delay.TotalSeconds:0}s ({transientAttempts}/{retry.MaxRetries}).");
                response.Dispose();
                await retry.Delay(delay, ct);
                continue;
            }

            return response;
        }
    }

    /// <summary>Throws a <see cref="SitecoreApiException"/> describing the failure unless the response is 2xx.</summary>
    public static async Task EnsureSuccessAsync(HttpResponseMessage response, string action, CancellationToken ct)
    {
        if (response.IsSuccessStatusCode)
            return;

        var detail = await ReadErrorAsync(response, ct);
        var hint = response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden
            ? " The automation client may lack access to this environment (Organization Admin/Owner is required)."
            : "";
        throw new SitecoreApiException(
            $"{action} failed: HTTP {(int)response.StatusCode} {response.ReasonPhrase}{detail}.{hint}",
            response.StatusCode);
    }

    public static async Task<T> ReadJsonAsync<T>(HttpResponseMessage response, string action, CancellationToken ct)
    {
        try
        {
            return await response.Content.ReadFromJsonAsync<T>(ApiJson.Options, ct)
                ?? throw new SitecoreApiException($"{action}: empty response body.");
        }
        catch (JsonException ex)
        {
            throw new SitecoreApiException($"{action}: unexpected response body ({ex.Message}).");
        }
    }

    /// <summary>True for failures worth retrying: network errors, timeouts, transient statuses, and rejected tokens (already invalidated).</summary>
    public static bool IsRetryable(Exception ex, CancellationToken ct) =>
        IsTransientException(ex, ct)
        || ex is SitecoreApiException { StatusCode: { } status } && (IsTransientStatus(status) || status is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden);

    public static bool IsTransientStatus(HttpStatusCode status) =>
        status is HttpStatusCode.RequestTimeout or HttpStatusCode.TooManyRequests || (int)status >= 500;

    private static bool IsTransientException(Exception ex, CancellationToken ct) =>
        ex is HttpRequestException or IOException
        || ex is TaskCanceledException && !ct.IsCancellationRequested; // HttpClient timeout

    private static TimeSpan? RetryAfter(HttpResponseMessage response)
    {
        var retryAfter = response.Headers.RetryAfter;
        if (retryAfter?.Delta is { } delta)
            return delta;
        if (retryAfter?.Date is { } date)
        {
            var wait = date - DateTimeOffset.UtcNow;
            return wait > TimeSpan.Zero ? wait : TimeSpan.Zero;
        }
        return null;
    }

    private static async Task<string> ReadErrorAsync(HttpResponseMessage response, CancellationToken ct)
    {
        string body;
        try
        {
            body = await response.Content.ReadAsStringAsync(ct);
        }
        catch (Exception)
        {
            return "";
        }

        if (string.IsNullOrWhiteSpace(body))
            return "";

        try
        {
            using var doc = JsonDocument.Parse(body);
            if (doc.RootElement.ValueKind == JsonValueKind.Object)
            {
                foreach (var property in doc.RootElement.EnumerateObject())
                {
                    if (property.Name.Equals("Error", StringComparison.OrdinalIgnoreCase) && property.Value.ValueKind == JsonValueKind.String)
                        return $" - {property.Value.GetString()}";
                }
            }
        }
        catch (JsonException)
        {
            // Not JSON; fall through to the raw text.
        }

        var text = body.ReplaceLineEndings(" ").Trim();
        return $" - {(text.Length > 300 ? text[..300] + "…" : text)}";
    }
}
