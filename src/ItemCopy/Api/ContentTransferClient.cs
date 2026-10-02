using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using ItemCopy.Cli;
using ItemCopy.Config;
using ItemCopy.Http;

namespace ItemCopy.Api;

/// <summary>
/// Content Transfer API v1. Create/status/get-chunk/delete run against the source; save-chunk and
/// complete run against the destination.
/// </summary>
public sealed class ContentTransferClient(SitecoreHttp http, Ui ui)
{
    private const string Prefix = "/sitecore/api/content/transfer/v1/transfers";

    public async Task CreateAsync(SitecoreEnvironment source, Guid transferId, JobItem item, string database, CancellationToken ct)
    {
        var body = new CreateContentTransferRequest(
            transferId,
            new TransferConfiguration([new DataTree(item.Path, item.Scope, item.MergeStrategy)], database));

        // Safe to retry: re-using a TransferId overwrites the operation.
        using var response = await http.SendAsync(
            source,
            () => new HttpRequestMessage(HttpMethod.Post, Url(source, ""))
            {
                Content = JsonContent.Create(body, options: ApiJson.Options),
            },
            ct);
        await SitecoreHttp.EnsureSuccessAsync(response, $"Creating transfer for {item.Path} on {source.Name}", ct);
    }

    public async Task<ContentTransferStatus> GetStatusAsync(SitecoreEnvironment source, Guid transferId, CancellationToken ct)
    {
        using var response = await http.SendAsync(source, () => new HttpRequestMessage(HttpMethod.Get, Url(source, $"/{transferId}/status")), ct);
        if (response.StatusCode == HttpStatusCode.NotFound)
            return new ContentTransferStatus { State = ContentTransferState.NotFound };

        await SitecoreHttp.EnsureSuccessAsync(response, $"Getting status of transfer {transferId}", ct);
        return await SitecoreHttp.ReadJsonAsync<ContentTransferStatus>(response, $"Getting status of transfer {transferId}", ct);
    }

    /// <summary>
    /// Streams one chunk from the source straight into the destination without buffering or
    /// altering it. The GET+PUT pair is retried as a unit because the streamed body can't be replayed.
    /// </summary>
    public async Task<ChunkInfo> CopyChunkAsync(
        SitecoreEnvironment source, SitecoreEnvironment destination, Guid transferId, Guid chunkSetId, int chunkId, CancellationToken ct)
    {
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                return await CopyChunkOnceAsync(source, destination, transferId, chunkSetId, chunkId, ct);
            }
            catch (Exception ex) when (attempt <= http.Retry.MaxRetries && SitecoreHttp.IsRetryable(ex, ct))
            {
                var delay = http.Retry.Backoff(attempt);
                ui.Warn($"Chunk {chunkId} copy failed ({ex.Message}); retrying in {delay.TotalSeconds:0}s ({attempt}/{http.Retry.MaxRetries}).");
                await http.Retry.Delay(delay, ct);
            }
        }
    }

    private async Task<ChunkInfo> CopyChunkOnceAsync(
        SitecoreEnvironment source, SitecoreEnvironment destination, Guid transferId, Guid chunkSetId, int chunkId, CancellationToken ct)
    {
        var chunkPath = $"/{transferId}/chunksets/{chunkSetId}/chunks/{chunkId}";

        using var getResponse = await http.SendAsync(
            source,
            () =>
            {
                var request = new HttpRequestMessage(HttpMethod.Get, Url(source, chunkPath));
                request.Headers.Accept.ParseAdd("application/octet-stream");
                request.Headers.Accept.ParseAdd("*/*;q=0.1");
                return request;
            },
            ct,
            retryTransient: false,
            completion: HttpCompletionOption.ResponseHeadersRead);
        // A 400 is documented for invalid IDs or a chunk with no processed items.
        var action = getResponse.StatusCode == HttpStatusCode.BadRequest
            ? $"Downloading chunk {chunkId} from {source.Name} (the source rejected the chunk; it may contain no exportable items)"
            : $"Downloading chunk {chunkId} from {source.Name}";
        await SitecoreHttp.EnsureSuccessAsync(getResponse, action, ct);
        EnsureRawChunk(getResponse, chunkId);

        var info = ChunkInfo.Parse(ContentDisposition(getResponse));

        await using var body = await getResponse.Content.ReadAsStreamAsync(ct);
        var isMedia = info.IsMedia ? "true" : "false";

        using var putResponse = await http.SendAsync(
            destination,
            () =>
            {
                var content = new StreamContent(body);
                content.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
                if (getResponse.Content.Headers.ContentLength is { } length)
                    content.Headers.ContentLength = length;
                return new HttpRequestMessage(HttpMethod.Put, Url(destination, $"{chunkPath}?isMedia={isMedia}")) { Content = content };
            },
            ct,
            replayable: false);
        await SitecoreHttp.EnsureSuccessAsync(putResponse, $"Saving chunk {chunkId} to {destination.Name}", ct);

        return info;
    }

    /// <summary>Generates the .raif file in the destination and returns its name.</summary>
    public async Task<string> CompleteChunkSetAsync(SitecoreEnvironment destination, Guid transferId, Guid chunkSetId, CancellationToken ct)
    {
        var action = $"Completing chunk set {chunkSetId} on {destination.Name}";
        using var response = await http.SendAsync(
            destination,
            () => new HttpRequestMessage(HttpMethod.Post, Url(destination, $"/{transferId}/chunksets/{chunkSetId}/complete")),
            ct,
            retryTransient: false);
        await SitecoreHttp.EnsureSuccessAsync(response, action, ct);

        var result = await SitecoreHttp.ReadJsonAsync<ChunkSetCompleteResponse>(response, action, ct);
        return string.IsNullOrWhiteSpace(result.ContentTransferFileName)
            ? throw new SitecoreApiException($"{action}: response did not include ContentTransferFileName.")
            : result.ContentTransferFileName;
    }

    public async Task DeleteAsync(SitecoreEnvironment source, Guid transferId, CancellationToken ct)
    {
        using var response = await http.SendAsync(source, () => new HttpRequestMessage(HttpMethod.Delete, Url(source, $"/{transferId}")), ct);
        if (response.StatusCode == HttpStatusCode.NotFound)
            return;
        await SitecoreHttp.EnsureSuccessAsync(response, $"Deleting transfer {transferId} on {source.Name}", ct);
    }

    /// <summary>
    /// Refuses chunk bodies that aren't the raw stream: a JSON (e.g. base64-wrapped) or
    /// transport-compressed body would otherwise be forwarded altered and corrupt the .raif.
    /// </summary>
    private static void EnsureRawChunk(HttpResponseMessage response, int chunkId)
    {
        var mediaType = response.Content.Headers.ContentType?.MediaType;
        if (mediaType is not null && mediaType.Contains("json", StringComparison.OrdinalIgnoreCase))
            throw new SitecoreApiException($"Chunk {chunkId} came back as {mediaType} instead of a binary stream; refusing to forward it.");

        if (response.Content.Headers.ContentEncoding.Count > 0)
            throw new SitecoreApiException($"Chunk {chunkId} came back with Content-Encoding {string.Join(", ", response.Content.Headers.ContentEncoding)}; refusing to forward it.");
    }

    private static Uri Url(SitecoreEnvironment env, string relative) => new(env.BaseUri, Prefix + relative);

    private static string? ContentDisposition(HttpResponseMessage response)
    {
        if (response.Content.Headers.TryGetValues("Content-Disposition", out var contentValues))
            return string.Join("; ", contentValues);
        if (response.Headers.TryGetValues("Content-Disposition", out var values))
            return string.Join("; ", values);
        return null;
    }
}
