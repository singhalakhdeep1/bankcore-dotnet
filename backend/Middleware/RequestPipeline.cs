using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using BankCore.Services.Idempotency;
using BankCore.Services.Logging;
using BankCore.Services.RateLimiting;
using BankingAPI.Extensions;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;
using SerilogContext = Serilog.Context.LogContext;

namespace BankingAPI.Middleware;

/// <summary>
/// Gives every request a correlation id (honouring a safe incoming X-Correlation-Id), returns it in the
/// response, and attaches it to log lines and the structured-logging context.
/// </summary>
public class CorrelationIdMiddleware
{
    public const string HeaderName = "X-Correlation-Id";
    private readonly RequestDelegate _next;

    public CorrelationIdMiddleware(RequestDelegate next)
    {
        _next = next;
    }

    public async Task InvokeAsync(HttpContext context, StructuredLoggingService structuredLogging)
    {
        var incoming = context.Request.Headers[HeaderName].FirstOrDefault();
        var correlationId = !string.IsNullOrEmpty(incoming) && incoming.Length <= 64 && incoming.All(c => char.IsLetterOrDigit(c) || c == '-')
            ? incoming
            : StructuredLoggingService.CreateCorrelationId();

        context.Items["CorrelationId"] = correlationId;
        context.Response.OnStarting(() =>
        {
            context.Response.Headers[HeaderName] = correlationId;
            return Task.CompletedTask;
        });

        structuredLogging.SetContext(new LogContext
        {
            CorrelationId = correlationId,
            RequestId = context.TraceIdentifier,
            IpAddress = context.Connection.RemoteIpAddress?.ToString(),
            UserAgent = context.Request.Headers.UserAgent.ToString()
        });

        using (SerilogContext.PushProperty("CorrelationId", correlationId))
        {
            await _next(context);
        }
    }
}

/// <summary>Standard defensive response headers. The API serves JSON, so a locked-down content policy is safe.</summary>
public class SecurityHeadersMiddleware
{
    private readonly RequestDelegate _next;

    public SecurityHeadersMiddleware(RequestDelegate next)
    {
        _next = next;
    }

    public Task InvokeAsync(HttpContext context)
    {
        context.Response.OnStarting(() =>
        {
            var headers = context.Response.Headers;
            headers["X-Content-Type-Options"] = "nosniff";
            headers["X-Frame-Options"] = "DENY";
            headers["Referrer-Policy"] = "no-referrer";
            headers["Cross-Origin-Resource-Policy"] = "same-origin";
            headers["Cache-Control"] = "no-store";

            // Swagger UI needs inline scripts and styles; everything else gets a deny-all policy.
            if (!context.Request.Path.StartsWithSegments("/swagger"))
            {
                headers["Content-Security-Policy"] = "default-src 'none'; frame-ancestors 'none'";
            }

            return Task.CompletedTask;
        });

        return _next(context);
    }
}

/// <summary>
/// Token-bucket rate limiting per signed-in user (or per IP for anonymous callers),
/// backed by the <see cref="RateLimitingService"/> pattern implementation.
/// </summary>
public class TokenBucketRateLimitMiddleware
{
    private readonly RequestDelegate _next;

    public TokenBucketRateLimitMiddleware(RequestDelegate next)
    {
        _next = next;
    }

    public async Task InvokeAsync(HttpContext context, RateLimitingService limiter)
    {
        if (context.Request.Path.StartsWithSegments("/health"))
        {
            await _next(context);
            return;
        }

        var key = context.User.GetUserId()?.ToString() is { } user
            ? $"user:{user}"
            : $"ip:{context.Connection.RemoteIpAddress}";

        if (!await limiter.IsAllowedAsync(key))
        {
            var retryAfter = Math.Max(1, (int)Math.Ceiling(limiter.GetTimeUntilRefill(key).TotalSeconds));
            context.Response.StatusCode = StatusCodes.Status429TooManyRequests;
            context.Response.Headers.RetryAfter = retryAfter.ToString();
            context.Response.ContentType = "application/json";
            await context.Response.WriteAsync(JsonSerializer.Serialize(new { message = "Too many requests. Slow down and retry shortly." }));
            return;
        }

        context.Response.Headers["X-RateLimit-Remaining"] = limiter.GetRemainingTokens(key).ToString();
        await _next(context);
    }
}

/// <summary>
/// Idempotency-Key support for state-changing requests. The first response is stored and replayed for
/// repeats of the same key (same user, route and body), so a client that retries after a timeout can never
/// double-charge. Reusing a key with a different body is rejected.
/// </summary>
public class IdempotencyMiddleware
{
    public const string HeaderName = "Idempotency-Key";
    private static readonly HashSet<string> Methods = new(StringComparer.OrdinalIgnoreCase) { "POST", "PUT", "PATCH", "DELETE" };

    private readonly RequestDelegate _next;

    public IdempotencyMiddleware(RequestDelegate next)
    {
        _next = next;
    }

    public async Task InvokeAsync(HttpContext context, IdempotencyService idempotency)
    {
        var header = context.Request.Headers[HeaderName].FirstOrDefault();
        if (string.IsNullOrWhiteSpace(header) || !Methods.Contains(context.Request.Method))
        {
            await _next(context);
            return;
        }

        if (header.Length > 64)
        {
            await Reject(context, StatusCodes.Status400BadRequest, "The Idempotency-Key header must be at most 64 characters.");
            return;
        }

        context.Request.EnableBuffering();
        using var reader = new StreamReader(context.Request.Body, Encoding.UTF8, leaveOpen: true);
        var body = await reader.ReadToEndAsync();
        context.Request.Body.Position = 0;

        var owner = context.User.GetUserId()?.ToString() ?? $"ip:{context.Connection.RemoteIpAddress}";
        var key = $"http:{Hash($"{owner}|{context.Request.Method}|{context.Request.Path}|{header}")}";
        var bodyHash = Hash(body);

        var cached = await idempotency.GetCachedResponseAsync<StoredResponse>(key);
        if (cached != null)
        {
            if (cached.BodyHash != bodyHash)
            {
                await Reject(context, StatusCodes.Status422UnprocessableEntity,
                    "This Idempotency-Key was already used with a different request body.");
                return;
            }

            if (cached.InFlight)
            {
                await Reject(context, StatusCodes.Status409Conflict, "A request with this Idempotency-Key is still being processed.");
                return;
            }

            context.Response.StatusCode = cached.StatusCode;
            context.Response.Headers["Idempotent-Replayed"] = "true";
            if (!string.IsNullOrEmpty(cached.ContentType)) context.Response.ContentType = cached.ContentType;
            await context.Response.WriteAsync(cached.Body);
            return;
        }

        // Mark as in flight so a concurrent duplicate gets a 409 instead of running twice.
        await idempotency.StoreResponseAsync(key, new StoredResponse { InFlight = true, BodyHash = bodyHash });

        var original = context.Response.Body;
        await using var buffer = new MemoryStream();
        context.Response.Body = buffer;

        try
        {
            await _next(context);

            buffer.Position = 0;
            var responseBody = await new StreamReader(buffer).ReadToEndAsync();

            if (context.Response.StatusCode < 500)
            {
                await idempotency.StoreResponseAsync(key, new StoredResponse
                {
                    StatusCode = context.Response.StatusCode,
                    ContentType = context.Response.ContentType,
                    Body = responseBody,
                    BodyHash = bodyHash
                });
            }
            else
            {
                await idempotency.InvalidateAsync(key); // server errors are safe to retry
            }

            buffer.Position = 0;
            await buffer.CopyToAsync(original);
        }
        catch
        {
            await idempotency.InvalidateAsync(key);
            throw;
        }
        finally
        {
            context.Response.Body = original;
        }
    }

    private static Task Reject(HttpContext context, int status, string message)
    {
        context.Response.StatusCode = status;
        context.Response.ContentType = "application/json";
        return context.Response.WriteAsync(JsonSerializer.Serialize(new { message }));
    }

    private static string Hash(string value) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant();

    public class StoredResponse
    {
        public bool InFlight { get; set; }
        public int StatusCode { get; set; }
        public string? ContentType { get; set; }
        public string Body { get; set; } = string.Empty;
        public string BodyHash { get; set; } = string.Empty;
    }
}

/// <summary>
/// Restricts an endpoint to the networks listed in <c>Security:AdminAllowedIps</c> (IPs or CIDR ranges).
/// An empty list means no restriction.
/// </summary>
public class AdminIpAllowListAttribute : ActionFilterAttribute
{
    public override void OnActionExecuting(ActionExecutingContext context)
    {
        var config = context.HttpContext.RequestServices.GetRequiredService<IConfiguration>();
        var allowed = config.GetSection("Security:AdminAllowedIps").Get<string[]>() ?? Array.Empty<string>();
        if (allowed.Length == 0) return;

        var remote = context.HttpContext.Connection.RemoteIpAddress;
        if (remote != null && remote.IsIPv4MappedToIPv6) remote = remote.MapToIPv4();

        var permitted = remote != null && allowed.Any(entry => Matches(entry, remote));
        if (!permitted)
        {
            context.Result = new ObjectResult(new { message = "This endpoint is not available from your network." })
            {
                StatusCode = StatusCodes.Status403Forbidden
            };
        }
    }

    private static bool Matches(string entry, IPAddress remote)
    {
        entry = entry.Trim();
        if (entry.Contains('/'))
        {
            return System.Net.IPNetwork.TryParse(entry, out var network) && network.Contains(remote);
        }

        return IPAddress.TryParse(entry, out var ip) && ip.Equals(remote);
    }
}
