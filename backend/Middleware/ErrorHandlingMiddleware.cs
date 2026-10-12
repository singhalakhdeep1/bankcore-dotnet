using System.Net;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;

namespace BankingAPI.Middleware;

public class ErrorHandlingMiddleware
{
    private readonly RequestDelegate _next;
    private readonly ILogger<ErrorHandlingMiddleware> _logger;
    private readonly IHostEnvironment _environment;

    public ErrorHandlingMiddleware(
        RequestDelegate next,
        ILogger<ErrorHandlingMiddleware> logger,
        IHostEnvironment environment)
    {
        _next = next;
        _logger = logger;
        _environment = environment;
    }

    public async Task InvokeAsync(HttpContext context)
    {
        try
        {
            await _next(context);
        }
        catch (DbUpdateConcurrencyException ex)
        {
            // Two requests changed the same account balance at once; the loser must retry.
            _logger.LogWarning(ex, "Concurrent update conflict");
            await WriteAsync(context, HttpStatusCode.Conflict,
                "The account was modified by another request. Please retry.", null);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "An unhandled exception occurred");
            await WriteAsync(context, HttpStatusCode.InternalServerError,
                "An error occurred processing your request",
                _environment.IsDevelopment() ? ex.Message : null);
        }
    }

    private static async Task WriteAsync(HttpContext context, HttpStatusCode status, string message, string? details)
    {
        if (context.Response.HasStarted) return;

        context.Response.ContentType = "application/json";
        context.Response.StatusCode = (int)status;

        var json = JsonSerializer.Serialize(new { message, details });
        await context.Response.WriteAsync(json);
    }
}
