namespace Wolfstare.Service.Api;

/// <summary>
/// Guards the local API against remote and cross-origin abuse.
///
/// This is explicitly NOT a defence against the user — per spec §2.1 authority lives in
/// <c>StopPolicy</c>, and a locked session answers 423 no matter who is asking. What this
/// stops is a malicious web page in the user's browser reaching 127.0.0.1 via a DNS-rebinding
/// attack and driving the API on their behalf.
/// </summary>
public sealed class LocalApiGuard(string token) : IMiddleware
{
    private const string HeaderPrefix = "Bearer ";

    public async Task InvokeAsync(HttpContext context, RequestDelegate next)
    {
        if (!context.Request.Path.StartsWithSegments("/api"))
        {
            await next(context);
            return;
        }

        // A cross-origin Origin header means a web page is driving this request. No legitimate
        // client sends one, so reject before doing any work.
        if (context.Request.Headers.Origin.Count > 0 && !IsLoopbackOrigin(context.Request.Headers.Origin!))
        {
            context.Response.StatusCode = StatusCodes.Status403Forbidden;
            await context.Response.WriteAsJsonAsync(
                new Contracts.ErrorDto("Cross-origin requests are not permitted."));
            return;
        }

        if (!IsAuthorised(context.Request))
        {
            context.Response.StatusCode = StatusCodes.Status401Unauthorized;
            await context.Response.WriteAsJsonAsync(
                new Contracts.ErrorDto("A valid bearer token is required."));
            return;
        }

        await next(context);
    }

    private bool IsAuthorised(HttpRequest request)
    {
        var header = request.Headers.Authorization.ToString();
        if (!header.StartsWith(HeaderPrefix, StringComparison.Ordinal)) return false;

        var supplied = header[HeaderPrefix.Length..].Trim();

        // Constant-time comparison: the token is short-lived and local, but a timing oracle
        // costs nothing to close.
        return System.Security.Cryptography.CryptographicOperations.FixedTimeEquals(
            System.Text.Encoding.UTF8.GetBytes(supplied),
            System.Text.Encoding.UTF8.GetBytes(token));
    }

    private static bool IsLoopbackOrigin(IEnumerable<string?> origins)
    {
        foreach (var origin in origins)
        {
            if (!Uri.TryCreate(origin, UriKind.Absolute, out var uri)) return false;
            if (!uri.IsLoopback) return false;
        }

        return true;
    }
}
