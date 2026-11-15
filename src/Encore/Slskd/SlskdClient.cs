using System.Net;
using System.Net.Http.Json;
using System.Text.Json;

namespace Encore.Slskd;

/// <summary>Timeouts and retry waits for <see cref="SlskdClient"/>. Defaults are the v0.2 design values.</summary>
public sealed record SlskdClientOptions
{
    /// <summary>Per call. Applied as <c>HttpClient.Timeout</c>.</summary>
    public TimeSpan Timeout { get; init; } = TimeSpan.FromSeconds(10);

    /// <summary>Wait before the one retry of a GET that got a 5xx.</summary>
    public TimeSpan GetRetryDelay { get; init; } = TimeSpan.FromMilliseconds(500);

    /// <summary>Wait between tries when enqueue gets 429.</summary>
    public TimeSpan BusyDelay { get; init; } = TimeSpan.FromSeconds(1);

    /// <summary>Tries for an enqueue that keeps getting 429.</summary>
    public int BusyTries { get; init; } = 3;
}

/// <summary>
/// slskd client over a typed HttpClient. The <see cref="SlskdAuthHandler"/> in front of it adds the API key, so this
/// class never sees it. Retries: GETs once on 5xx; enqueue up to <see cref="SlskdClientOptions.BusyTries"/> times on 429;
/// nothing else (a retried POST could start a second search or download).
/// </summary>
public sealed class SlskdClient : ISlskdClient
{
    // Fixed search options (slskd-api.md section 2): a search ends after 8 s of silence.
    private const int SearchTimeoutMs = 8000;
    private const int ResponseLimit = 100;
    private const int FileLimit = 10000;
    private const int MaximumPeerQueueLength = 50;

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private readonly HttpClient _http;
    private readonly SlskdClientOptions _options;

    public SlskdClient(HttpClient http, SlskdClientOptions options)
    {
        _http = http ?? throw new ArgumentNullException(nameof(http));
        _options = options ?? throw new ArgumentNullException(nameof(options));
        if (_http.BaseAddress is null)
        {
            throw new ArgumentException("HttpClient.BaseAddress must be the slskd base URL (with a trailing slash)", nameof(http));
        }
    }

    public async Task<SlskdServerState> GetServerStateAsync(CancellationToken ct)
    {
        // /api/v0/server is Administrator-only in slskd 0.26.2; /api/v0/application takes any key (slskd-api.md section 9).
        var app = await GetJsonAsync<ApplicationDto>("api/v0/application", "GetServerState", ct).ConfigureAwait(false);
        var dto = app.Server ?? throw new SlskdRequestException("GetServerState", "response had no server state");
        return new SlskdServerState(dto.IsConnected, dto.IsLoggedIn, dto.State ?? "");
    }

    public async Task<SlskdSearch> StartSearchAsync(SlskdSearchRequest request, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.SearchText);
        var body = new
        {
            id = request.Id,
            searchText = request.SearchText,
            searchTimeout = SearchTimeoutMs,
            responseLimit = ResponseLimit,
            fileLimit = FileLimit,
            filterResponses = true,
            minimumResponseFileCount = 1,
            maximumPeerQueueLength = MaximumPeerQueueLength,
            minimumPeerUploadSpeed = 0,
        };

        using var response = await SendAsync(HttpMethod.Post, "api/v0/searches", "StartSearch", body, retryServerError: false, ct).ConfigureAwait(false);
        return ToSearch(await ReadAsync<SearchDto>(response!, "StartSearch", ct).ConfigureAwait(false), request.Id);
    }

    public async Task<SlskdSearch?> GetSearchAsync(Guid id, CancellationToken ct)
    {
        using var response = await SendAsync(HttpMethod.Get, $"api/v0/searches/{id:D}?includeResponses=false", "GetSearch", null, retryServerError: true, ct, allowNotFound: true).ConfigureAwait(false);
        if (response is null)
        {
            return null;
        }

        return ToSearch(await ReadAsync<SearchDto>(response, "GetSearch", ct).ConfigureAwait(false), id);
    }

    public async Task<IReadOnlyList<SlskdSearchResponse>> GetSearchResponsesAsync(Guid id, CancellationToken ct)
    {
        var dtos = await GetJsonAsync<List<ResponseDto>>($"api/v0/searches/{id:D}/responses", "GetSearchResponses", ct).ConfigureAwait(false);
        return dtos.Select(ToResponse).ToList();
    }

    public async Task CancelSearchAsync(Guid id, CancellationToken ct)
    {
        using var response = await SendAsync(HttpMethod.Put, $"api/v0/searches/{id:D}", "CancelSearch", null, retryServerError: false, ct, allowNotFound: true).ConfigureAwait(false);
    }

    public async Task DeleteSearchAsync(Guid id, CancellationToken ct)
    {
        using var response = await SendAsync(HttpMethod.Delete, $"api/v0/searches/{id:D}", "DeleteSearch", null, retryServerError: false, ct, allowNotFound: true).ConfigureAwait(false);
    }

    public async Task<SlskdEnqueueResult> EnqueueAsync(SlskdEnqueueRequest request, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.Username);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.Filename);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.Destination);
        var body = new
        {
            id = request.BatchId,
            searchId = request.SearchId,
            username = request.Username,
            files = new[] { new { filename = request.Filename, size = request.Size } },
            options = new { destination = request.Destination },
        };

        for (var attempt = 1; ; attempt++)
        {
            try
            {
                using var response = await SendAsync(HttpMethod.Post, "api/v0/transfers/downloads/batches", "Enqueue", body, retryServerError: false, ct, notFoundIsPeerOffline: true).ConfigureAwait(false);
                var dto = await ReadAsync<EnqueueDto>(response!, "Enqueue", ct).ConfigureAwait(false);
                var transfer = dto.Batch?.Transfers?.FirstOrDefault();
                var failures = (dto.Failures ?? []).Select(f => string.IsNullOrWhiteSpace(f.Message) ? "failed" : f.Message!).ToList();
                return new SlskdEnqueueResult(transfer?.Id, failures);
            }
            catch (SlskdBusyException) when (attempt < Math.Max(1, _options.BusyTries))
            {
                await Task.Delay(_options.BusyDelay, ct).ConfigureAwait(false);
            }
        }
    }

    public async Task<SlskdTransfer?> GetTransferAsync(string username, Guid transferId, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(username);
        using var response = await SendAsync(HttpMethod.Get, $"api/v0/transfers/downloads/{Uri.EscapeDataString(username)}/{transferId:D}", "GetTransfer", null, retryServerError: true, ct, allowNotFound: true).ConfigureAwait(false);
        if (response is null)
        {
            return null;
        }

        var dto = await ReadAsync<TransferDto>(response, "GetTransfer", ct).ConfigureAwait(false);
        return new SlskdTransfer(
            dto.Id, dto.Username ?? username, dto.Filename ?? "", dto.Size, dto.State ?? "",
            dto.BytesTransferred, dto.AverageSpeed, dto.PlaceInQueue, dto.Attempts, dto.NextAttemptAt);
    }

    public async Task CancelTransferAsync(string username, Guid transferId, bool remove, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(username);
        var path = $"api/v0/transfers/downloads/{Uri.EscapeDataString(username)}/{transferId:D}?remove={(remove ? "true" : "false")}";
        using var response = await SendAsync(HttpMethod.Delete, path, "CancelTransfer", null, retryServerError: false, ct, allowNotFound: true).ConfigureAwait(false);
    }

    private async Task<T> GetJsonAsync<T>(string path, string operation, CancellationToken ct) where T : class
    {
        using var response = await SendAsync(HttpMethod.Get, path, operation, null, retryServerError: true, ct).ConfigureAwait(false);
        return await ReadAsync<T>(response!, operation, ct).ConfigureAwait(false);
    }

    private static async Task<T> ReadAsync<T>(HttpResponseMessage response, string operation, CancellationToken ct) where T : class
    {
        try
        {
            var body = await response.Content.ReadFromJsonAsync<T>(Json, ct).ConfigureAwait(false);
            return body ?? throw new SlskdRequestException(operation, "empty response body");
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            throw new SlskdRequestException(operation, "timed out reading the response");
        }
        catch (Exception e) when (e is JsonException or NotSupportedException)
        {
            throw new SlskdRequestException(operation, "response was not the expected JSON");
        }
    }

    /// <summary>
    /// Sends one call. Returns a successful response (the caller disposes it), or null when <paramref name="allowNotFound"/> is set and
    /// slskd said 404. 401/403, 429, 409 and 404 (when mapped) become their own exception types.
    /// </summary>
    private async Task<HttpResponseMessage?> SendAsync(
        HttpMethod method, string path, string operation, object? body, bool retryServerError, CancellationToken ct,
        bool allowNotFound = false, bool notFoundIsPeerOffline = false)
    {
        for (var attempt = 0; ; attempt++)
        {
            HttpResponseMessage response;
            try
            {
                using var request = new HttpRequestMessage(method, path);
                if (body is not null)
                {
                    request.Content = JsonContent.Create(body, options: Json);
                }

                response = await _http.SendAsync(request, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (OperationCanceledException)
            {
                throw new SlskdRequestException(operation, "timed out");
            }
            catch (HttpRequestException e)
            {
                // Keep only the error kind, never the message (it can hold the URL).
                throw new SlskdRequestException(operation, $"connection failed ({e.HttpRequestError})");
            }

            if (response.IsSuccessStatusCode)
            {
                return response;
            }

            var status = (int)response.StatusCode;
            response.Dispose();

            switch (status)
            {
                case 401 or 403:
                    throw new SlskdAuthException(operation, status);
                case 429:
                    throw new SlskdBusyException(operation);
                case 409:
                    throw new SlskdUnavailableException(operation);
                case 404 when notFoundIsPeerOffline:
                    throw new SlskdPeerOfflineException(operation);
                case 404 when allowNotFound:
                    return null;
            }

            if (retryServerError && attempt == 0 && status is >= 500 and <= 599)
            {
                await Task.Delay(_options.GetRetryDelay, ct).ConfigureAwait(false);
                continue;
            }

            throw new SlskdRequestException(operation, status);
        }
    }

    private static SlskdSearch ToSearch(SearchDto dto, Guid fallbackId) =>
        new(dto.Id == Guid.Empty ? fallbackId : dto.Id, dto.State ?? "", dto.IsComplete, dto.ResponseCount, dto.FileCount);

    private static SlskdSearchResponse ToResponse(ResponseDto dto) =>
        new(dto.Username ?? "", dto.HasFreeUploadSlot, dto.UploadSpeed, dto.QueueLength,
            (dto.Files ?? []).Select(f => new SlskdFile(
                f.Filename ?? "", f.Size, f.BitRate, f.IsVariableBitRate, f.Length, f.SampleRate, f.BitDepth,
                string.IsNullOrWhiteSpace(f.Extension) ? ExtensionOf(f.Filename) : f.Extension.TrimStart('.').ToLowerInvariant())).ToList());

    /// <summary>Lowercase extension without the dot, from the last path segment (peers use backslashes).</summary>
    public static string ExtensionOf(string? filename)
    {
        if (string.IsNullOrEmpty(filename))
        {
            return "";
        }

        var name = filename[(filename.LastIndexOfAny(['\\', '/']) + 1)..];
        var dot = name.LastIndexOf('.');
        return dot < 0 || dot == name.Length - 1 ? "" : name[(dot + 1)..].ToLowerInvariant();
    }

    private sealed record ApplicationDto(ServerDto? Server);

    private sealed record ServerDto(bool IsConnected, bool IsLoggedIn, string? State);

    private sealed record SearchDto(Guid Id, string? State, bool IsComplete, int ResponseCount, int FileCount);

    private sealed record ResponseDto(string? Username, bool HasFreeUploadSlot, long UploadSpeed, long QueueLength, List<FileDto>? Files);

    private sealed record FileDto(string? Filename, long Size, int? BitRate, bool? IsVariableBitRate, int? Length, int? SampleRate, int? BitDepth, string? Extension);

    private sealed record EnqueueDto(BatchDto? Batch, List<FailureDto>? Failures);

    private sealed record BatchDto(List<TransferIdDto>? Transfers);

    private sealed record TransferIdDto(Guid Id);

    private sealed record FailureDto(string? Filename, string? Message);

    private sealed record TransferDto(
        Guid Id, string? Username, string? Filename, long Size, string? State,
        long BytesTransferred, double AverageSpeed, int? PlaceInQueue, int Attempts, DateTimeOffset? NextAttemptAt);
}
