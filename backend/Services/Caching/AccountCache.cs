using Microsoft.Extensions.Caching.Distributed;

namespace BankingAPI.Services.Caching;

/// <summary>
/// Cache invalidation by version: cached account lists and transaction pages include a version in their key,
/// and any posting bumps the version, so stale entries are simply never read again and expire on their own.
/// </summary>
public interface IAccountCache
{
    Task<string> AccountVersionAsync(Guid accountId);
    Task<string> UserVersionAsync(Guid userId);
    Task BumpAsync(Guid accountId, Guid userId);
}

public class AccountCache : IAccountCache
{
    private static readonly DistributedCacheEntryOptions VersionLifetime =
        new() { AbsoluteExpirationRelativeToNow = TimeSpan.FromHours(6) };

    private readonly IDistributedCache _cache;
    private readonly ILogger<AccountCache> _logger;

    public AccountCache(IDistributedCache cache, ILogger<AccountCache> logger)
    {
        _cache = cache;
        _logger = logger;
    }

    public Task<string> AccountVersionAsync(Guid accountId) => ReadAsync($"ver:acct:{accountId}");
    public Task<string> UserVersionAsync(Guid userId) => ReadAsync($"ver:user:{userId}");

    public async Task BumpAsync(Guid accountId, Guid userId)
    {
        try
        {
            var version = Guid.NewGuid().ToString("N")[..8];
            await _cache.SetStringAsync($"ver:acct:{accountId}", version, VersionLifetime);
            await _cache.SetStringAsync($"ver:user:{userId}", version, VersionLifetime);
        }
        catch (Exception ex)
        {
            // A cache outage must not fail a committed payment; entries simply expire by TTL.
            _logger.LogWarning(ex, "Could not invalidate cache for account {AccountId}", accountId);
        }
    }

    private async Task<string> ReadAsync(string key)
    {
        try
        {
            return await _cache.GetStringAsync(key) ?? "0";
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Cache version read failed for {Key}", key);
            return "0";
        }
    }
}
