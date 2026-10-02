using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using System.Web;

namespace ItemCopy.Tests.Fakes;

/// <summary>
/// In-memory stand-in for Sitecore Cloud auth plus a source and destination SitecoreAI environment,
/// implementing just enough of the Content Transfer and Item Transfer APIs for pipeline tests.
/// </summary>
public sealed class FakeSitecore : HttpMessageHandler
{
    public const string AuthUrl = "https://auth.test/oauth/token";
    public const string SourceHost = "source.test";
    public const string DestinationHost = "dest.test";

    private const string ContentPrefix = "/sitecore/api/content/transfer/v1/transfers";
    private const string ItemsPrefix = "/sitecore/shell/api/v3/ItemsTransfer";

    private readonly Lock _lock = new();
    private int _itemTransferCounter;

    // ---- Behaviour switches ----

    /// <summary>Chunks per transfer, by item path. Default 2.</summary>
    public Dictionary<string, int> ChunkCounts { get; } = [];
    public int ExportPollsBeforeComplete { get; set; } = 1;
    public HashSet<string> CreateRejects { get; } = [];
    public HashSet<string> ExportFails { get; } = [];
    public HashSet<string> ExportNeverCompletes { get; } = [];
    /// <summary>Status codes returned by the next chunk GETs, before normal responses resume.</summary>
    public Queue<HttpStatusCode> ChunkGetFailures { get; } = new();
    /// <summary>Hosts whose first authorised request is answered with 401.</summary>
    public HashSet<string> RejectFirstRequestTo { get; } = [];
    /// <summary>Location header ends with the blob name (per the endpoint reference) instead of the transfer ID.</summary>
    public bool LocationIsBlobName { get; set; }
    public HashSet<string> LoadFailsOnce { get; } = [];
    public Dictionary<string, List<string>> LoadValidationErrors { get; } = [];

    // ---- Observed state ----

    public List<string> Requests { get; } = [];
    public Dictionary<Guid, SourceTransfer> SourceTransfers { get; } = [];
    public Dictionary<(Guid Transfer, Guid ChunkSet, int Chunk), (byte[] Body, bool IsMedia, string? ContentType)> SavedChunks { get; } = [];
    public Dictionary<string, Blob> Blobs { get; } = [];
    public Dictionary<string, ItemTransfer> ItemTransfers { get; } = [];
    public List<string> LoadOrder { get; } = [];
    public Dictionary<string, int> TokensIssued { get; } = [];

    public sealed class SourceTransfer
    {
        public required string Path { get; init; }
        public required string Scope { get; init; }
        public required string MergeStrategy { get; init; }
        public required string Database { get; init; }
        public Guid ChunkSetId { get; } = Guid.NewGuid();
        public int ChunkCount { get; init; }
        public int StatusPolls { get; set; }
        public bool Deleted { get; set; }
    }

    public sealed class Blob
    {
        public required string Path { get; init; }
        public string State { get; set; } = "Uploading";
        public int Polls { get; set; }
    }

    public sealed class ItemTransfer
    {
        public required string Id { get; init; }
        public required string BlobName { get; init; }
        public required string Path { get; init; }
        public string State { get; set; } = "InProgress";
        public int Polls { get; set; }
        public bool Retried { get; set; }
        public DateTimeOffset ConsumedDate { get; } = DateTimeOffset.UtcNow;
    }

    /// <summary>Deterministic chunk bytes, including every byte value, so tests can check they arrive unchanged.</summary>
    public static byte[] ChunkBytes(string path, int chunkId) =>
        [.. Encoding.UTF8.GetBytes($"{path}#{chunkId}|"), .. Enumerable.Range(0, 256).Select(b => (byte)b)];

    public static bool IsMediaPath(string path) => path.Contains("/media library/", StringComparison.OrdinalIgnoreCase);

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        var body = request.Content is null ? null : await request.Content.ReadAsByteArrayAsync(ct);
        lock (_lock)
        {
            var uri = request.RequestUri!;
            Requests.Add($"{request.Method} {uri.Host} {uri.AbsolutePath}");

            if (uri.ToString() == AuthUrl)
                return IssueToken(body!);

            if (request.Headers.Authorization?.Parameter is not { Length: > 0 })
                return Status(HttpStatusCode.Unauthorized);
            if (RejectFirstRequestTo.Remove(uri.Host))
                return Status(HttpStatusCode.Unauthorized);

            return uri.Host switch
            {
                SourceHost => Source(request, uri, body),
                DestinationHost => Destination(request, uri, body),
                _ => Status(HttpStatusCode.NotFound),
            };
        }
    }

    private HttpResponseMessage IssueToken(byte[] body)
    {
        var form = HttpUtility.ParseQueryString(Encoding.UTF8.GetString(body));
        if (form["grant_type"] != "client_credentials" || form["audience"] != "https://api.test")
            return Status(HttpStatusCode.BadRequest);
        if (form["client_secret"] != "secret")
            return Json(HttpStatusCode.Unauthorized, new { error = "access_denied", error_description = "Unauthorized" });

        var clientId = form["client_id"]!;
        TokensIssued[clientId] = TokensIssued.GetValueOrDefault(clientId) + 1;
        return Json(HttpStatusCode.OK, new { access_token = $"tok-{clientId}-{TokensIssued[clientId]}", expires_in = 86400, token_type = "Bearer" });
    }

    private HttpResponseMessage Source(HttpRequestMessage request, Uri uri, byte[]? body)
    {
        var path = uri.AbsolutePath;

        if (request.Method == HttpMethod.Post && path == ContentPrefix)
        {
            var json = JsonNode.Parse(body!)!;
            var id = Guid.Parse(json["TransferId"]!.GetValue<string>());
            var tree = json["Configuration"]!["DataTrees"]!.AsArray().Single()!;
            var itemPath = tree["ItemPath"]!.GetValue<string>();
            if (CreateRejects.Contains(itemPath))
                return Json(HttpStatusCode.BadRequest, new { Error = $"Invalid item path '{itemPath}'" });
            SourceTransfers[id] = new SourceTransfer
            {
                Path = itemPath,
                Scope = tree["Scope"]!.GetValue<string>(),
                MergeStrategy = tree["MergeStrategy"]!.GetValue<string>(),
                Database = json["Configuration"]!["Database"]!.GetValue<string>(),
                ChunkCount = ChunkCounts.GetValueOrDefault(itemPath, 2),
            };
            return Status(HttpStatusCode.Accepted);
        }

        if (Match(path, $@"^{ContentPrefix}/([^/]+)/status$") is { } statusMatch)
        {
            if (!SourceTransfers.TryGetValue(Guid.Parse(statusMatch[1]), out var t) || t.Deleted)
                return Json(HttpStatusCode.NotFound, new { Error = "Transfer not found" });

            t.StatusPolls++;
            if (ExportFails.Contains(t.Path))
                return Json(HttpStatusCode.OK, new { State = "Failed" });
            if (ExportNeverCompletes.Contains(t.Path) || t.StatusPolls <= ExportPollsBeforeComplete)
                return Json(HttpStatusCode.OK, new { State = "Running" });
            return Json(HttpStatusCode.OK, new
            {
                State = "Completed",
                ChunkSetsMetadata = new[] { new { t.ChunkSetId, t.ChunkCount, TotalItemCount = t.ChunkCount * 10 } },
            });
        }

        if (request.Method == HttpMethod.Get && Match(path, $@"^{ContentPrefix}/([^/]+)/chunksets/([^/]+)/chunks/(\d+)$") is { } chunkMatch)
        {
            if (ChunkGetFailures.TryDequeue(out var failure))
                return Status(failure);

            var t = SourceTransfers[Guid.Parse(chunkMatch[1])];
            var chunkId = int.Parse(chunkMatch[3]);
            if (Guid.Parse(chunkMatch[2]) != t.ChunkSetId || chunkId >= t.ChunkCount)
                return Json(HttpStatusCode.NotFound, new { Error = "Chunk set doesn't exist" });

            var response = new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(ChunkBytes(t.Path, chunkId)) };
            response.Content.Headers.TryAddWithoutValidation(
                "Content-Disposition",
                $"attachment; filename=\"chunk{chunkId}.bin\"; ItemsProcessed=10; ItemsSkipped=0; IsMedia={(IsMediaPath(t.Path) ? "True" : "False")}");
            return response;
        }

        if (request.Method == HttpMethod.Delete && Match(path, $@"^{ContentPrefix}/([^/]+)$") is { } deleteMatch)
        {
            if (!SourceTransfers.TryGetValue(Guid.Parse(deleteMatch[1]), out var t))
                return Status(HttpStatusCode.NotFound);
            t.Deleted = true;
            return Status(HttpStatusCode.Accepted);
        }

        return Status(HttpStatusCode.NotFound);
    }

    private HttpResponseMessage Destination(HttpRequestMessage request, Uri uri, byte[]? body)
    {
        var path = uri.AbsolutePath;
        var query = HttpUtility.ParseQueryString(uri.Query);

        if (request.Method == HttpMethod.Put && Match(path, $@"^{ContentPrefix}/([^/]+)/chunksets/([^/]+)/chunks/(\d+)$") is { } putMatch)
        {
            SavedChunks[(Guid.Parse(putMatch[1]), Guid.Parse(putMatch[2]), int.Parse(putMatch[3]))] =
                (body!, bool.Parse(query["isMedia"]!), request.Content!.Headers.ContentType?.MediaType);
            return Status(HttpStatusCode.Created);
        }

        if (request.Method == HttpMethod.Post && Match(path, $@"^{ContentPrefix}/([^/]+)/chunksets/([^/]+)/complete$") is { } completeMatch)
        {
            var transferId = Guid.Parse(completeMatch[1]);
            var t = SourceTransfers[transferId];
            var saved = Enumerable.Range(0, t.ChunkCount).Count(i => SavedChunks.ContainsKey((transferId, t.ChunkSetId, i)));
            if (saved != t.ChunkCount)
                return Json(HttpStatusCode.BadRequest, new { Error = $"Only {saved}/{t.ChunkCount} chunks saved" });

            var blobName = $"content-transfer-{transferId}.raif";
            Blobs[blobName] = new Blob { Path = t.Path };
            return Json(HttpStatusCode.OK, new { ContentTransferFileName = blobName });
        }

        if (path.StartsWith(ItemsPrefix, StringComparison.Ordinal))
            return Items(request, path[ItemsPrefix.Length..], query);

        return Status(HttpStatusCode.NotFound);
    }

    private HttpResponseMessage Items(HttpRequestMessage request, string path, System.Collections.Specialized.NameValueCollection query)
    {
        if (Match(path, "^/sources/blobs/([^/]+)$") is { } blobMatch)
        {
            var name = Uri.UnescapeDataString(blobMatch[1]);
            if (!Blobs.TryGetValue(name, out var blob))
                return Json(HttpStatusCode.NotFound, new { Error = "Blob not found" });

            if (request.Method == HttpMethod.Delete)
            {
                Blobs.Remove(name);
                return Status(HttpStatusCode.NoContent);
            }

            if (++blob.Polls > 1 && blob.State == "Uploading")
                blob.State = "Uploaded";
            return Json(HttpStatusCode.OK, new { BlobState = blob.State, Error = (string?)null, SourceName = name });
        }

        if (request.Method == HttpMethod.Post && Match(path, "^/transfers/databases/([^/]+)/sources$") is not null)
        {
            var blobName = query["blobName"]!;
            if (!Blobs.TryGetValue(blobName, out var blob) || blob.State != "Uploaded")
                return Json(HttpStatusCode.BadRequest, new { Error = "Blob not ready" });

            blob.State = "Consumed";
            var id = $"consumed.20261001 120000 {++_itemTransferCounter}.{Guid.NewGuid()}";
            ItemTransfers[id] = new ItemTransfer { Id = id, BlobName = blobName, Path = blob.Path };
            LoadOrder.Add(blob.Path);

            var response = Status(HttpStatusCode.Accepted);
            response.Headers.Location = LocationIsBlobName
                ? new Uri($"https://{DestinationHost}{ItemsPrefix}/sources/blobs/{Uri.EscapeDataString(blobName)}")
                : new Uri($"https://{DestinationHost}{ItemsPrefix}/transfers/{Uri.EscapeDataString(id)}");
            return response;
        }

        if (request.Method == HttpMethod.Put && Match(path, "^/transfers/databases/([^/]+)/sources/([^/]+)$") is { } retryMatch)
        {
            var blobName = Uri.UnescapeDataString(retryMatch[2]);
            var t = ItemTransfers.Values.Single(x => x.BlobName == blobName);
            if (t.State != "Failed")
                return Json(HttpStatusCode.BadRequest, new { Error = "Only failed transfers can be retried" });
            t.State = "InProgress";
            t.Polls = 0;
            t.Retried = true;
            return Json(HttpStatusCode.OK, new { DatabaseName = "master", SourceName = blobName });
        }

        if (request.Method == HttpMethod.Get && path == "/transfers")
        {
            var transfers = ItemTransfers.Values.Select(t => new
            {
                t.Id, SourceName = t.BlobName, DatabaseName = "master", ConsumedDate = t.ConsumedDate, TransferState = t.State,
            }).ToList();
            return Json(HttpStatusCode.OK, new { Page = 1, PageSize = 50, TotalCount = transfers.Count, Transfers = transfers });
        }

        if (request.Method == HttpMethod.Get && Match(path, "^/transfers/([^/]+)$") is { } transferMatch)
        {
            var id = Uri.UnescapeDataString(transferMatch[1]);
            if (!ItemTransfers.TryGetValue(id, out var t))
                return Json(HttpStatusCode.NotFound, new { Error = "Transfer not found" });

            if (t.State == "InProgress" && ++t.Polls > 1)
            {
                if (LoadFailsOnce.Contains(t.Path) && !t.Retried)
                {
                    t.State = "Failed";
                }
                else
                {
                    t.State = "Finished";
                    Blobs[t.BlobName].State = LoadValidationErrors.ContainsKey(t.Path) ? "TransferredWithErrors" : "Transferred";
                }
            }

            var total = SourceTransfers.Values.First(s => s.Path == t.Path).ChunkCount * 10;
            var errors = LoadValidationErrors.GetValueOrDefault(t.Path);
            return Json(HttpStatusCode.OK, new
            {
                t.Id, SourceName = t.BlobName, DatabaseName = "master", TransferState = t.State, Strategy = "OverrideExistingItem",
                TotalItemsCount = total, TransferredItemsCount = total - (errors?.Count ?? 0), ValidationErrors = errors, SourcesCount = 1,
            });
        }

        return Status(HttpStatusCode.NotFound);
    }

    private static string[]? Match(string input, string pattern)
    {
        var match = Regex.Match(input, pattern);
        return match.Success ? match.Groups.Values.Select(g => g.Value).ToArray() : null;
    }

    private static HttpResponseMessage Status(HttpStatusCode status) => new(status) { Content = new ByteArrayContent([]) };

    private static HttpResponseMessage Json(HttpStatusCode status, object body) =>
        new(status) { Content = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json") };
}
