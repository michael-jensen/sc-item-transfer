using System.Net;
using ItemCopy.Config;
using ItemCopy.Http;

namespace ItemCopy.Api;

/// <summary>Item Transfer API v3. Every call runs against the destination environment.</summary>
public sealed class ItemTransferClient(SitecoreHttp http)
{
    private const string Prefix = "/sitecore/shell/api/v3/ItemsTransfer";
    private const int ListPageSize = 50;
    private const int MaxListPages = 20;

    /// <summary>Returns the blob's state, or null if the destination doesn't know the blob (yet).</summary>
    public async Task<BlobDetails?> GetBlobAsync(SitecoreEnvironment destination, string blobName, CancellationToken ct)
    {
        var action = $"Getting state of {blobName}";
        using var response = await http.SendAsync(destination, () => Get(destination, $"/sources/blobs/{Escape(blobName)}"), ct);
        if (response.StatusCode == HttpStatusCode.NotFound)
            return null;

        await SitecoreHttp.EnsureSuccessAsync(response, action, ct);
        return await SitecoreHttp.ReadJsonAsync<BlobDetails>(response, action, ct);
    }

    /// <summary>Starts loading the blob into <paramref name="database"/>. Returns the <c>Location</c> header.</summary>
    public async Task<Uri?> StartConsumeAsync(SitecoreEnvironment destination, string database, string blobName, CancellationToken ct)
    {
        using var response = await http.SendAsync(
            destination,
            () => new HttpRequestMessage(HttpMethod.Post, Url(destination, $"/transfers/databases/{Escape(database)}/sources?blobName={Escape(blobName)}")),
            ct,
            retryTransient: false);
        await SitecoreHttp.EnsureSuccessAsync(response, $"Starting to load {blobName} into {database}", ct);
        return response.Headers.Location;
    }

    /// <summary>Returns the transfer, or null if it isn't found. A 400 also counts as not found, since the ID may be a guess.</summary>
    public async Task<ItemTransferDetails?> GetTransferAsync(SitecoreEnvironment destination, string transferId, CancellationToken ct)
    {
        var action = $"Getting load status for {transferId}";
        using var response = await http.SendAsync(destination, () => Get(destination, $"/transfers/{Escape(transferId)}"), ct);
        if (response.StatusCode is HttpStatusCode.NotFound or HttpStatusCode.BadRequest)
            return null;

        await SitecoreHttp.EnsureSuccessAsync(response, action, ct);
        return await SitecoreHttp.ReadJsonAsync<ItemTransferDetails>(response, action, ct);
    }

    /// <summary>
    /// Works out the ID to poll after starting a load. The docs disagree on whether the last segment
    /// of the <c>Location</c> header is the transfer ID or the blob name, so try it directly first,
    /// then fall back to finding the newest transfer for the blob in the transfer list.
    /// Returns null if neither finds it (the transfer may not be listed yet).
    /// </summary>
    public async Task<string?> ResolveTransferIdAsync(SitecoreEnvironment destination, Uri? location, string blobName, CancellationToken ct)
    {
        var lastSegment = LastSegment(location);
        if (lastSegment is not null && await GetTransferAsync(destination, lastSegment, ct) is not null)
            return lastSegment;

        return await FindTransferIdBySourceAsync(destination, blobName, ct);
    }

    public async Task RetryAsync(SitecoreEnvironment destination, string database, string sourceName, CancellationToken ct)
    {
        using var response = await http.SendAsync(
            destination,
            () => new HttpRequestMessage(HttpMethod.Put, Url(destination, $"/transfers/databases/{Escape(database)}/sources/{Escape(sourceName)}")),
            ct,
            retryTransient: false);
        await SitecoreHttp.EnsureSuccessAsync(response, $"Retrying load of {sourceName}", ct);
    }

    public async Task DeleteBlobAsync(SitecoreEnvironment destination, string blobName, CancellationToken ct)
    {
        using var response = await http.SendAsync(
            destination,
            () => new HttpRequestMessage(HttpMethod.Delete, Url(destination, $"/sources/blobs/{Escape(blobName)}")),
            ct);
        if (response.StatusCode == HttpStatusCode.NotFound)
            return;
        await SitecoreHttp.EnsureSuccessAsync(response, $"Deleting {blobName} from {destination.Name}", ct);
    }

    internal static string? LastSegment(Uri? location)
    {
        if (location is null)
            return null;

        var path = location.IsAbsoluteUri ? location.AbsolutePath : location.OriginalString.Split('?')[0];
        var segment = path.Split('/', StringSplitOptions.RemoveEmptyEntries).LastOrDefault();
        return segment is null ? null : Uri.UnescapeDataString(segment);
    }

    private async Task<string?> FindTransferIdBySourceAsync(SitecoreEnvironment destination, string blobName, CancellationToken ct)
    {
        ItemTransferStatus? newest = null;
        for (var page = 1; page <= MaxListPages; page++)
        {
            var action = "Listing transfers";
            using var response = await http.SendAsync(destination, () => Get(destination, $"/transfers?page={page}&pageSize={ListPageSize}"), ct);
            await SitecoreHttp.EnsureSuccessAsync(response, action, ct);
            var result = await SitecoreHttp.ReadJsonAsync<ItemTransfersPage>(response, action, ct);

            foreach (var transfer in result.Transfers ?? [])
            {
                if (transfer.Id is not null
                    && string.Equals(transfer.SourceName, blobName, StringComparison.OrdinalIgnoreCase)
                    && (newest is null || transfer.ConsumedDate > newest.ConsumedDate))
                {
                    newest = transfer;
                }
            }

            if ((result.Transfers?.Count ?? 0) == 0 || page * ListPageSize >= result.TotalCount)
                break;
        }

        return newest?.Id;
    }

    private static HttpRequestMessage Get(SitecoreEnvironment env, string relative) => new(HttpMethod.Get, Url(env, relative));

    private static Uri Url(SitecoreEnvironment env, string relative) => new(env.BaseUri, Prefix + relative);

    private static string Escape(string value) => Uri.EscapeDataString(value);
}
