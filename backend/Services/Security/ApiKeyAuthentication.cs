using System.Security.Claims;
using System.Text.Encodings.Web;
using BankingAPI.Data;
using Microsoft.AspNetCore.Authentication;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace BankingAPI.Services.Security;

public static class ApiKeyDefaults
{
    public const string Scheme = "ApiKey";
    public const string HeaderName = "X-Api-Key";
}

/// <summary>
/// Authenticates server-to-server callers that send <c>X-Api-Key</c>. The principal carries the owner's
/// id and role plus one "scope" claim per granted scope, so the same role-based policies apply to keys.
/// </summary>
public class ApiKeyAuthenticationHandler : AuthenticationHandler<AuthenticationSchemeOptions>
{
    private readonly IApiKeyService _keys;
    private readonly ApplicationDbContext _context;

    public ApiKeyAuthenticationHandler(
        IOptionsMonitor<AuthenticationSchemeOptions> options,
        ILoggerFactory logger,
        UrlEncoder encoder,
        IApiKeyService keys,
        ApplicationDbContext context)
        : base(options, logger, encoder)
    {
        _keys = keys;
        _context = context;
    }

    protected override async Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        if (!Request.Headers.TryGetValue(ApiKeyDefaults.HeaderName, out var header) || string.IsNullOrWhiteSpace(header))
        {
            return AuthenticateResult.NoResult();
        }

        var key = await _keys.ValidateAsync(header.ToString());
        if (key == null) return AuthenticateResult.Fail("Invalid API key");

        var owner = await _context.Users.AsNoTracking().FirstOrDefaultAsync(u => u.Id == key.OwnerUserId);
        if (owner == null || !owner.IsActive || owner.IsLocked) return AuthenticateResult.Fail("The API key owner is not active");

        var claims = new List<Claim>
        {
            new("sub", owner.Id.ToString()),
            new(ClaimTypes.Role, owner.Role.ToString()),
            new("auth_method", "api_key"),
            new("api_key_id", key.Id.ToString())
        };
        claims.AddRange(key.Scopes.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(scope => new Claim("scope", scope)));

        var identity = new ClaimsIdentity(claims, Scheme.Name, "sub", ClaimTypes.Role);
        return AuthenticateResult.Success(new AuthenticationTicket(new ClaimsPrincipal(identity), Scheme.Name));
    }
}
