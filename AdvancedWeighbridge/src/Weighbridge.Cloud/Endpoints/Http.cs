using System.Security.Claims;
using Weighbridge.Cloud.Services;
using Weighbridge.Contracts;

namespace Weighbridge.Cloud.Endpoints;

internal static class Http
{
    public static string? Ip(this HttpContext http) => http.Connection.RemoteIpAddress?.ToString();

    public static CloudPrincipal CloudUser(this ClaimsPrincipal user) => CloudPrincipal.From(user);

    public static SitePrincipal Site(this ClaimsPrincipal user) => SitePrincipal.From(user);

    public static IResult Error(string message, int status = StatusCodes.Status400BadRequest, bool? mfaRequired = null) =>
        Results.Json(new ErrorDto(message, mfaRequired), Api.Json, statusCode: status);
}

/// <summary>Maps exceptions to JSON errors with the right status; unexpected errors are logged and never leak details.</summary>
public sealed class ApiErrorMiddleware(RequestDelegate next, ILogger<ApiErrorMiddleware> log)
{
    public async Task InvokeAsync(HttpContext context)
    {
        try
        {
            await next(context).ConfigureAwait(false);
        }
        catch (Exception e) when (!context.Response.HasStarted && e is not OperationCanceledException)
        {
            var (status, message) = e switch
            {
                BadHttpRequestException b => (b.StatusCode, "The request is malformed: " + b.Message),
                System.Text.Json.JsonException => (400, "The request body is not valid JSON."),
                SiteMismatchException => (409, e.Message),
                ArgumentException => (400, e.Message),
                FormatException => (400, e.Message),
                KeyNotFoundException => (404, e.Message),
                UnauthorizedAccessException => (403, e.Message),
                Weighbridge.Core.Security.PermissionDeniedException => (403, e.Message),
                InvalidOperationException => (409, e.Message),
                _ => (500, "An internal error occurred. The event was logged on the server."),
            };
            if (status >= 500) log.LogError(e, "Unhandled error for {Method} {Path}", context.Request.Method, context.Request.Path);
            context.Response.Clear();
            context.Response.StatusCode = status;
            await context.Response.WriteAsJsonAsync(new ErrorDto(message), Api.Json).ConfigureAwait(false);
        }
    }
}
