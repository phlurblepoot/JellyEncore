using Encore.Requests.Albums;
using Encore.Requests.Api;
using Encore.Requests.Auth;
using Encore.Store;

namespace Encore.Requests.Songs;

/// <summary>Progress of an album request (tracks done of tracks expected).</summary>
public sealed record RequestProgress(int Tracks, int Of);

/// <summary>The request JSON of v0.3 design 4. Item ids are 32 hex digits, like the search rows.</summary>
public sealed record RequestDto(
    string Id,
    string Kind,
    string State,
    string? Detail,
    string Artist,
    string? Title,
    string? Album,
    int? Year,
    string? CoverUrl,
    DateTimeOffset CreatedUtc,
    DateTimeOffset UpdatedUtc,
    DateTimeOffset? ReadyUtc,
    string UserName,
    string? ItemId,
    string? FailReason,
    int Attempts,
    RequestProgress? Progress,
    DateTimeOffset? NextTryUtc = null)
{
    public static RequestDto From(RequestRow row)
    {
        ArgumentNullException.ThrowIfNull(row);
        return new RequestDto(
            row.Id, row.Kind, row.State, row.Detail, row.Artist, row.Title, row.Album, row.Year, row.CoverUrl,
            row.CreatedUtc, row.UpdatedUtc, row.ReadyUtc, row.UserName, row.JellyfinItemId, row.FailReason, row.Attempts,
            row.TracksTotal is > 0 and var of ? new RequestProgress(row.TracksDone ?? 0, of) : null,
            row.NextTryUtc);
    }
}

/// <summary>
/// The request routes (v0.3 design 4): POST/GET <c>/requests</c>, GET/DELETE <c>/requests/{id}</c> and POST
/// <c>/requests/{id}/retry</c>. Album requests go to the <see cref="AlbumRequester"/>; without Lidarr (no requester) they
/// answer 503 <c>albums_unavailable</c>.
/// </summary>
public sealed class RequestEndpoints(
    ApiEndpoints api,
    RequestUsers users,
    RequestService service,
    IRequestNotifier notifier,
    ILogger<RequestEndpoints> logger,
    AlbumRequester? albums = null)
{
    private sealed record CreateBody(string? Kind, long? DeezerTrackId, string? ForeignAlbumId);

    public void Map(RouteGroupBuilder group)
    {
        ArgumentNullException.ThrowIfNull(group);
        group.MapPost("/requests", (Delegate)CreateAsync);
        group.MapGet("/requests", (Delegate)ListAsync);
        group.MapGet("/requests/{id}", (Delegate)GetAsync);
        group.MapDelete("/requests/{id}", (Delegate)CancelAsync);
        group.MapPost("/requests/{id}/retry", (Delegate)RetryAsync);
    }

    private async Task<IResult> CreateAsync(HttpContext context)
    {
        var (user, failure) = await api.AuthenticateAsync(context).ConfigureAwait(false);
        if (user is null)
        {
            return failure!;
        }

        if (!users.CanRequest(user))
        {
            return ApiErrors.Result(StatusCodes.Status403Forbidden, AuthCodes.NotAllowed);
        }

        var body = await ApiEndpoints.ReadBodyAsync<CreateBody>(context).ConfigureAwait(false);
        switch (body?.Kind?.Trim().ToLowerInvariant())
        {
            case "song" when body.DeezerTrackId is > 0:
                var (outcome, notify) = await service.CreateSongAsync(user, body.DeezerTrackId.Value, context.RequestAborted).ConfigureAwait(false);
                await NotifyAsync(notify, context.RequestAborted).ConfigureAwait(false);
                return Answer(outcome);
            case "album" when !string.IsNullOrWhiteSpace(body.ForeignAlbumId):
                if (albums is null)
                {
                    return ApiErrors.Result(StatusCodes.Status503ServiceUnavailable, RequestCodes.AlbumsUnavailable);
                }

                var (albumOutcome, albumNotify) = await albums.CreateAsync(user, body.ForeignAlbumId, context.RequestAborted).ConfigureAwait(false);
                await NotifyAsync(albumNotify, context.RequestAborted).ConfigureAwait(false);
                return Answer(albumOutcome);
            default:
                return ApiErrors.Result(StatusCodes.Status400BadRequest, AuthCodes.BadRequest);
        }
    }

    private async Task<IResult> ListAsync(HttpContext context)
    {
        var (user, failure) = await api.AuthenticateAsync(context).ConfigureAwait(false);
        if (user is null)
        {
            return failure!;
        }

        var query = context.Request.Query;
        var scope = query["scope"].ToString().Trim().ToLowerInvariant();
        if (scope is not ("" or "mine" or "all"))
        {
            return ApiErrors.Result(StatusCodes.Status400BadRequest, AuthCodes.BadRequest);
        }

        var all = scope == "all";
        if (all && !user.IsAdmin)
        {
            return ApiErrors.Result(StatusCodes.Status403Forbidden, RequestCodes.AdminOnly);
        }

        var active = query["active"].ToString().Trim().ToLowerInvariant() is "true" or "1";
        var rows = service.List(user, all, active);
        return Results.Json(rows.Select(RequestDto.From).ToList(), ApiJson.Options);
    }

    private async Task<IResult> GetAsync(HttpContext context, string id)
    {
        var (user, failure) = await api.AuthenticateAsync(context).ConfigureAwait(false);
        return user is null ? failure! : Answer(service.Get(user, id));
    }

    private async Task<IResult> CancelAsync(HttpContext context, string id)
    {
        var (user, failure) = await api.AuthenticateAsync(context).ConfigureAwait(false);
        if (user is null)
        {
            return failure!;
        }

        return albums is not null && IsAlbum(user, id)
            ? Answer(await albums.CancelAsync(user, id, context.RequestAborted).ConfigureAwait(false))
            : Answer(service.Cancel(user, id));
    }

    private async Task<IResult> RetryAsync(HttpContext context, string id)
    {
        var (user, failure) = await api.AuthenticateAsync(context).ConfigureAwait(false);
        if (user is null)
        {
            return failure!;
        }

        if (!users.CanRequest(user))
        {
            return ApiErrors.Result(StatusCodes.Status403Forbidden, AuthCodes.NotAllowed);
        }

        var (outcome, notify) = albums is not null && IsAlbum(user, id)
            ? await albums.RetryAsync(user, id, context.RequestAborted).ConfigureAwait(false)
            : service.Retry(user, id);
        await NotifyAsync(notify, context.RequestAborted).ConfigureAwait(false);
        return Answer(outcome);
    }

    private bool IsAlbum(RequestUser user, string id) => service.Get(user, id).Row?.Kind == "album";

    private static IResult Answer(RequestOutcome outcome) => outcome.Ok
        ? Results.Json(RequestDto.From(outcome.Row!), ApiJson.Options, statusCode: outcome.Status)
        : ApiErrors.Result(outcome.Status, outcome.Error!);

    private async Task NotifyAsync(IReadOnlyList<RequestRow> rows, CancellationToken ct)
    {
        foreach (var row in rows)
        {
            try
            {
                await notifier.NotifyAsync(row, ct).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                logger.WorkerError("notify", ex.GetType().Name);
            }
        }
    }
}
