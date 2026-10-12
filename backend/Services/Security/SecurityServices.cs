using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using BankingAPI.Data;
using BankingAPI.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Distributed;
using Microsoft.Extensions.Caching.Memory;
using MongoDB.Bson;
using OtpNet;

namespace BankingAPI.Services.Security;

// ============================================================================ password policy

/// <summary>Password rules enforced at registration and when changing a password.</summary>
public static class PasswordPolicy
{
    public const int MinLength = 10;

    /// <summary>Returns the list of rules the password breaks (empty when it is acceptable).</summary>
    public static List<string> Validate(string password, string? email = null)
    {
        var problems = new List<string>();

        if (password.Length < MinLength) problems.Add($"Must be at least {MinLength} characters long.");
        if (!password.Any(char.IsUpper)) problems.Add("Must contain an upper-case letter.");
        if (!password.Any(char.IsLower)) problems.Add("Must contain a lower-case letter.");
        if (!password.Any(char.IsDigit)) problems.Add("Must contain a digit.");
        if (password.All(char.IsLetterOrDigit)) problems.Add("Must contain a symbol such as ! or #.");

        if (!string.IsNullOrWhiteSpace(email))
        {
            var local = email.Split('@')[0];
            if (local.Length >= 4 && password.Contains(local, StringComparison.OrdinalIgnoreCase))
            {
                problems.Add("Must not contain your email name.");
            }
        }

        return problems;
    }
}

// ============================================================================ two-factor (TOTP)

public interface ITwoFactorService
{
    string GenerateSecret();
    string BuildOtpAuthUri(string email, string secret);
    bool VerifyCode(string secret, string code);
}

/// <summary>RFC 6238 time-based one-time passwords, compatible with Google Authenticator, Authy and similar apps.</summary>
public class TwoFactorService : ITwoFactorService
{
    private const string Issuer = "BankCore";

    public string GenerateSecret() => Base32Encoding.ToString(KeyGeneration.GenerateRandomKey(20));

    public string BuildOtpAuthUri(string email, string secret) =>
        $"otpauth://totp/{Uri.EscapeDataString(Issuer)}:{Uri.EscapeDataString(email)}?secret={secret}&issuer={Uri.EscapeDataString(Issuer)}&digits=6&period=30";

    public bool VerifyCode(string secret, string code)
    {
        if (string.IsNullOrWhiteSpace(secret) || !Regex.IsMatch(code, @"^\d{6,8}$"))
        {
            return false;
        }

        var totp = new Totp(Base32Encoding.ToBytes(secret));
        return totp.VerifyTotp(code, out _, VerificationWindow.RfcSpecifiedNetworkDelay);
    }
}

// ============================================================================ token revocation

public interface ITokenStore
{
    Task RevokeAsync(string jti, DateTime expiresUtc);

    /// <summary>
    /// True if the token id was revoked. If the cache cannot be reached the answer is <paramref name="failOpen"/>:
    /// short-lived access tokens fail open (an outage must not take every request down), refresh tokens fail closed.
    /// </summary>
    Task<bool> IsRevokedAsync(string jti, bool failOpen = true);
}

/// <summary>
/// Remembers revoked token ids (logout, refresh rotation) in the distributed cache until the token would
/// have expired anyway, so a stolen token stops working immediately without a database lookup per request.
/// </summary>
public class TokenStore : ITokenStore
{
    private readonly IDistributedCache _cache;
    private readonly ILogger<TokenStore> _logger;

    public TokenStore(IDistributedCache cache, ILogger<TokenStore> logger)
    {
        _cache = cache;
        _logger = logger;
    }

    public async Task RevokeAsync(string jti, DateTime expiresUtc)
    {
        if (string.IsNullOrEmpty(jti)) return;

        var remaining = expiresUtc - DateTime.UtcNow;
        if (remaining <= TimeSpan.Zero) return;

        try
        {
            await _cache.SetStringAsync($"revoked:{jti}", "1",
                new DistributedCacheEntryOptions { AbsoluteExpirationRelativeToNow = remaining + TimeSpan.FromMinutes(1) });
        }
        catch (Exception ex)
        {
            // Revocation could not be recorded: say so loudly instead of pretending the logout fully worked.
            _logger.LogError(ex, "Could not record revocation of token {Jti}; it stays valid until it expires", jti);
            throw;
        }
    }

    public async Task<bool> IsRevokedAsync(string jti, bool failOpen = true)
    {
        if (string.IsNullOrEmpty(jti)) return false;

        try
        {
            return await _cache.GetStringAsync($"revoked:{jti}") != null;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Revocation check failed for token {Jti}; treating it as {Outcome}", jti, failOpen ? "valid" : "revoked");
            return !failOpen;
        }
    }
}

// ============================================================================ audit log

public interface IAuditService
{
    Task LogAsync(string action, Guid? userId = null, string? entityType = null, string? entityId = null, string? details = null);
}

/// <summary>
/// Writes an append-only audit row to PostgreSQL (and mirrors it to MongoDB when configured).
/// It uses its own short-lived context so the record survives even when the caller's transaction rolls back.
/// </summary>
public class AuditService : IAuditService
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly IHttpContextAccessor _http;
    private readonly ILogger<AuditService> _logger;

    public AuditService(IServiceScopeFactory scopeFactory, IHttpContextAccessor http, ILogger<AuditService> logger)
    {
        _scopeFactory = scopeFactory;
        _http = http;
        _logger = logger;
    }

    private static BsonValue Text(string? value) => value == null ? BsonNull.Value : new BsonString(value);

    public async Task LogAsync(string action, Guid? userId = null, string? entityType = null, string? entityId = null, string? details = null)
    {
        var context = _http.HttpContext;
        var entry = new AuditLog
        {
            Id = Guid.NewGuid(),
            Timestamp = DateTime.UtcNow,
            UserId = userId ?? context?.User.GetUserIdOrNull(),
            Action = action,
            EntityType = entityType,
            EntityId = entityId,
            Details = details is { Length: > 2000 } ? details[..2000] : details,
            IpAddress = context?.Connection.RemoteIpAddress?.ToString(),
            CorrelationId = context?.Items["CorrelationId"] as string
        };

        try
        {
            using var scope = _scopeFactory.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            db.AuditLogs.Add(entry);
            await db.SaveChangesAsync();

            var mongo = scope.ServiceProvider.GetService<MongoDBService>();
            if (mongo != null)
            {
                await mongo.InsertOneAsync("audit_logs", new BsonDocument
                {
                    { "_id", entry.Id.ToString() },
                    { "timestamp", entry.Timestamp },
                    { "userId", Text(entry.UserId?.ToString()) },
                    { "action", entry.Action },
                    { "entityType", Text(entry.EntityType) },
                    { "entityId", Text(entry.EntityId) },
                    { "details", Text(entry.Details) },
                    { "ipAddress", Text(entry.IpAddress) },
                    { "correlationId", Text(entry.CorrelationId) }
                });
            }
        }
        catch (Exception ex)
        {
            // Auditing must never take the request down; the failure itself is logged.
            _logger.LogError(ex, "Failed to write audit entry {Action}", action);
        }
    }
}

// ============================================================================ sanctions screening

public interface ISanctionsService
{
    Task<SanctionedEntity?> ScreenAsync(string? name, string? accountNumber = null);
    Task<List<SanctionedEntity>> ListAsync();
    Task<SanctionedEntity> AddAsync(string name, string? accountNumber, string? country, string listName);
    Task<bool> RemoveAsync(Guid id);
}

/// <summary>OFAC-style watch-list screening of counterparties by name and account number.</summary>
public class SanctionsService : ISanctionsService
{
    private const string CacheKey = "sanctions:list";

    private readonly ApplicationDbContext _context;
    private readonly IMemoryCache _cache;

    public SanctionsService(ApplicationDbContext context, IMemoryCache cache)
    {
        _context = context;
        _cache = cache;
    }

    public async Task<SanctionedEntity?> ScreenAsync(string? name, string? accountNumber = null)
    {
        var list = await GetActiveListAsync();

        if (!string.IsNullOrWhiteSpace(accountNumber))
        {
            var byAccount = list.FirstOrDefault(e =>
                !string.IsNullOrEmpty(e.AccountNumber)
                && string.Equals(e.AccountNumber, accountNumber.Trim(), StringComparison.OrdinalIgnoreCase));
            if (byAccount != null) return byAccount;
        }

        if (string.IsNullOrWhiteSpace(name)) return null;

        var candidate = Normalize(name);
        return list.FirstOrDefault(e => NamesMatch(candidate, e.NormalizedName));
    }

    public async Task<List<SanctionedEntity>> ListAsync() =>
        await _context.SanctionedEntities.AsNoTracking().OrderBy(e => e.Name).ToListAsync();

    public async Task<SanctionedEntity> AddAsync(string name, string? accountNumber, string? country, string listName)
    {
        var entity = new SanctionedEntity
        {
            Id = Guid.NewGuid(),
            Name = name.Trim(),
            NormalizedName = Normalize(name),
            AccountNumber = string.IsNullOrWhiteSpace(accountNumber) ? null : accountNumber.Trim().ToUpperInvariant(),
            Country = country?.Trim(),
            ListName = string.IsNullOrWhiteSpace(listName) ? "OFAC-SDN" : listName.Trim()
        };

        _context.SanctionedEntities.Add(entity);
        await _context.SaveChangesAsync();
        _cache.Remove(CacheKey);
        return entity;
    }

    public async Task<bool> RemoveAsync(Guid id)
    {
        var entity = await _context.SanctionedEntities.FindAsync(id);
        if (entity == null) return false;

        entity.IsActive = false;
        await _context.SaveChangesAsync();
        _cache.Remove(CacheKey);
        return true;
    }

    private async Task<List<SanctionedEntity>> GetActiveListAsync()
    {
        return await _cache.GetOrCreateAsync(CacheKey, async entry =>
        {
            entry.AbsoluteExpirationRelativeToNow = TimeSpan.FromMinutes(5);
            return await _context.SanctionedEntities.AsNoTracking().Where(e => e.IsActive).ToListAsync();
        }) ?? new List<SanctionedEntity>();
    }

    public static string Normalize(string value)
    {
        var chars = value.ToLowerInvariant().Select(c => char.IsLetterOrDigit(c) ? c : ' ').ToArray();
        return string.Join(' ', new string(chars).Split(' ', StringSplitOptions.RemoveEmptyEntries));
    }

    /// <summary>Exact, word-order-insensitive, one-typo, or "all listed words appear" matches.</summary>
    public static bool NamesMatch(string candidate, string listed)
    {
        if (candidate.Length == 0 || listed.Length == 0) return false;
        if (candidate == listed) return true;

        var candidateTokens = candidate.Split(' ');
        var listedTokens = listed.Split(' ');

        if (candidateTokens.OrderBy(t => t).SequenceEqual(listedTokens.OrderBy(t => t))) return true;
        if (listedTokens.Length >= 2 && listedTokens.All(t => candidateTokens.Contains(t))) return true;
        return listed.Length >= 8 && Levenshtein(candidate, listed) <= 1;
    }

    private static int Levenshtein(string a, string b)
    {
        var previous = Enumerable.Range(0, b.Length + 1).ToArray();
        for (var i = 1; i <= a.Length; i++)
        {
            var current = new int[b.Length + 1];
            current[0] = i;
            for (var j = 1; j <= b.Length; j++)
            {
                var cost = a[i - 1] == b[j - 1] ? 0 : 1;
                current[j] = Math.Min(Math.Min(current[j - 1] + 1, previous[j] + 1), previous[j - 1] + cost);
            }
            previous = current;
        }
        return previous[b.Length];
    }
}

// ============================================================================ API keys

public interface IApiKeyService
{
    Task<(ApiKey Key, string PlainKey)> CreateAsync(Guid ownerUserId, string name, string scopes, int? expiresInDays);
    Task<ApiKey?> ValidateAsync(string plainKey);
    Task<List<ApiKey>> ListAsync(Guid? ownerUserId = null);
    Task<bool> RevokeAsync(Guid id);
}

/// <summary>Issues and validates API keys. The key is shown once; only its SHA-256 hash is stored.</summary>
public class ApiKeyService : IApiKeyService
{
    private const string KeyPrefix = "bck_";

    private readonly ApplicationDbContext _context;

    public ApiKeyService(ApplicationDbContext context)
    {
        _context = context;
    }

    public async Task<(ApiKey Key, string PlainKey)> CreateAsync(Guid ownerUserId, string name, string scopes, int? expiresInDays)
    {
        var secret = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)).TrimEnd('=').Replace('+', '-').Replace('/', '_');
        var plain = KeyPrefix + secret;

        var key = new ApiKey
        {
            Id = Guid.NewGuid(),
            OwnerUserId = ownerUserId,
            Name = name.Trim(),
            Prefix = secret[..8],
            KeyHash = Hash(plain),
            Scopes = string.IsNullOrWhiteSpace(scopes) ? "read" : scopes.Trim(),
            ExpiresAt = expiresInDays is > 0 ? DateTime.UtcNow.AddDays(expiresInDays.Value) : null
        };

        _context.ApiKeys.Add(key);
        await _context.SaveChangesAsync();
        return (key, plain);
    }

    public async Task<ApiKey?> ValidateAsync(string plainKey)
    {
        if (!plainKey.StartsWith(KeyPrefix, StringComparison.Ordinal) || plainKey.Length < KeyPrefix.Length + 8)
        {
            return null;
        }

        var prefix = plainKey.Substring(KeyPrefix.Length, 8);
        var key = await _context.ApiKeys.FirstOrDefaultAsync(k => k.Prefix == prefix);
        if (key == null || !key.IsActive) return null;

        var expected = Encoding.UTF8.GetBytes(key.KeyHash);
        var actual = Encoding.UTF8.GetBytes(Hash(plainKey));
        if (!CryptographicOperations.FixedTimeEquals(expected, actual)) return null;

        if (key.LastUsedAt == null || key.LastUsedAt < DateTime.UtcNow.AddMinutes(-5))
        {
            key.LastUsedAt = DateTime.UtcNow;
            await _context.SaveChangesAsync();
        }

        return key;
    }

    public async Task<List<ApiKey>> ListAsync(Guid? ownerUserId = null)
    {
        var query = _context.ApiKeys.AsNoTracking();
        if (ownerUserId.HasValue) query = query.Where(k => k.OwnerUserId == ownerUserId.Value);
        return await query.OrderByDescending(k => k.CreatedAt).ToListAsync();
    }

    public async Task<bool> RevokeAsync(Guid id)
    {
        var key = await _context.ApiKeys.FindAsync(id);
        if (key == null) return false;

        key.RevokedAt ??= DateTime.UtcNow;
        await _context.SaveChangesAsync();
        return true;
    }

    private static string Hash(string plain) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(plain))).ToLowerInvariant();
}

internal static class ClaimsExtensions
{
    public static Guid? GetUserIdOrNull(this System.Security.Claims.ClaimsPrincipal principal) =>
        BankingAPI.Extensions.ClaimsPrincipalExtensions.GetUserId(principal);
}
