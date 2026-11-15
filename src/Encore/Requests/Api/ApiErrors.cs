using System.Text.Json;
using Encore.Requests.Auth;

namespace Encore.Requests.Api;

/// <summary>JSON options of the HTTP API: camelCase, no nulls dropped (a null is a value: "unlimited", "not read yet").</summary>
internal static class ApiJson
{
    public static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web);
}

/// <summary>The error shape of /api/v1 (design 4): <c>{ "error": "code", "message": "plain text" }</c>.</summary>
internal static class ApiErrors
{
    public const string NotFound = "not_found";
    public const string Internal = "internal_error";
    public const string MethodNotAllowed = "method_not_allowed";

    private static readonly Dictionary<string, string> Messages = new()
    {
        [AuthCodes.MissingToken] = "Sign in to Jellyfin first.",
        [AuthCodes.MalformedToken] = "The sign-in token is not valid. Sign in again.",
        [AuthCodes.InvalidToken] = "The sign-in has expired or is not valid. Sign in again.",
        [AuthCodes.Disabled] = "This Jellyfin account is disabled.",
        [AuthCodes.NotAllowed] = "This account may not make requests.",
        [AuthCodes.JellyfinUnavailable] = "Jellyfin could not be reached. Try again in a moment.",
        [AuthCodes.LoginLimited] = "Too many attempts. Try again later.",
        [AuthCodes.InvalidCredentials] = "The name or password is not right.",
        [AuthCodes.BadRequest] = "The request is not valid.",
        [AuthCodes.UnknownSecret] = "This Quick Connect sign-in is not known or has expired. Start again.",
        [AuthCodes.QuickConnectOff] = "Quick Connect is switched off in Jellyfin.",
        [NotFound] = "There is nothing here.",
        [MethodNotAllowed] = "This method is not allowed here.",
        [Internal] = "Something went wrong on the server.",
    };

    public static string MessageFor(string code) => Messages.TryGetValue(code, out var text) ? text : "The request failed.";

    public static IResult Result(AuthError error) => new ErrorResult(error.Status, error.Code, error.RetryAfter);

    public static IResult Result(int status, string code, TimeSpan? retryAfter = null) => new ErrorResult(status, code, retryAfter);

    /// <summary>Writes the error body straight to a response (used by middleware).</summary>
    public static Task WriteAsync(HttpContext context, int status, string code, TimeSpan? retryAfter = null) =>
        new ErrorResult(status, code, retryAfter).ExecuteAsync(context);

    private sealed class ErrorResult(int status, string code, TimeSpan? retryAfter) : IResult
    {
        public Task ExecuteAsync(HttpContext httpContext)
        {
            var response = httpContext.Response;
            response.StatusCode = status;
            if (retryAfter is { } wait)
            {
                response.Headers.RetryAfter = Math.Max(1, (int)Math.Ceiling(wait.TotalSeconds)).ToString(System.Globalization.CultureInfo.InvariantCulture);
            }

            return response.WriteAsJsonAsync(new { error = code, message = MessageFor(code) }, ApiJson.Options);
        }
    }
}
