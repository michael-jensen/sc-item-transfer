using System.Net;

namespace ItemCopy.Http;

/// <summary>A Sitecore API call returned an unexpected response.</summary>
public class SitecoreApiException(string message, HttpStatusCode? statusCode = null) : Exception(message)
{
    public HttpStatusCode? StatusCode { get; } = statusCode;

    /// <summary>The server refused the request (4xx), so it created or started nothing.</summary>
    public bool IsRejected => StatusCode is { } status && (int)status < 500;
}

/// <summary>A token could not be obtained for an environment.</summary>
public sealed class AuthException(string message) : Exception(message);
