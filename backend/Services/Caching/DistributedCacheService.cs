using Microsoft.Extensions.Options;
using System;
using System.Threading.Tasks;
using Microsoft.Extensions.Caching.Distributed;
using Microsoft.Extensions.Logging;
using System.Text.Json;

namespace BankCore.Services.Caching
{
    /// <summary>
    /// Distributed Caching Service with Cache-Aside Pattern
    /// Manages distributed cache coherency across multiple instances
    /// </summary>
    public class DistributedCacheService
    {
        private readonly IDistributedCache _cache;
        private readonly ILogger<DistributedCacheService> _logger;
        private readonly CacheOptions _options;

        public DistributedCacheService(
            IDistributedCache cache, 
            IOptions<CacheOptions> options,
            ILogger<DistributedCacheService> logger)
        {
            _cache = cache;
            _options = options.Value;
            _logger = logger;
        }

        /// <summary>
        /// Get value from cache (Cache-Aside Pattern)
        /// </summary>
        public async Task<T?> GetAsync<T>(string key)
        {
            try
            {
                var cachedValue = await _cache.GetStringAsync(key);
                
                if (string.IsNullOrEmpty(cachedValue))
                {
                    _logger.LogDebug($"Cache miss for key: {key}");
                    return default;
                }

                _logger.LogDebug($"Cache hit for key: {key}");
                return JsonSerializer.Deserialize<T>(cachedValue);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, $"Error getting from cache for key: {key}");
                return default;
            }
        }

        /// <summary>
        /// Set value in cache
        /// </summary>
        public async Task SetAsync<T>(string key, T value, TimeSpan? absoluteExpiration = null)
        {
            try
            {
                var options = new DistributedCacheEntryOptions
                {
                    AbsoluteExpirationRelativeToNow = absoluteExpiration ?? _options.DefaultExpiration,
                    SlidingExpiration = _options.SlidingExpiration
                };

                var serializedValue = JsonSerializer.Serialize(value);
                await _cache.SetStringAsync(key, serializedValue, options);
                
                _logger.LogDebug($"Set cache for key: {key}");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, $"Error setting cache for key: {key}");
            }
        }

        /// <summary>
        /// Remove value from cache
        /// </summary>
        public async Task RemoveAsync(string key)
        {
            try
            {
                await _cache.RemoveAsync(key);
                _logger.LogDebug($"Removed cache for key: {key}");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, $"Error removing cache for key: {key}");
            }
        }

        /// <summary>
        /// Refresh cache expiration
        /// </summary>
        public async Task RefreshAsync(string key)
        {
            try
            {
                await _cache.RefreshAsync(key);
                _logger.LogDebug($"Refreshed cache for key: {key}");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, $"Error refreshing cache for key: {key}");
            }
        }

        /// <summary>
        /// Get or set pattern - fetch from cache or load from source
        /// </summary>
        public async Task<T> GetOrSetAsync<T>(string key, Func<Task<T>> factory, TimeSpan? absoluteExpiration = null)
        {
            var cachedValue = await GetAsync<T>(key);
            
            if (cachedValue != null)
            {
                return cachedValue;
            }

            var value = await factory();
            await SetAsync(key, value, absoluteExpiration);
            
            return value;
        }

        /// <summary>
        /// Invalidate cache by pattern
        /// </summary>
        public Task InvalidatePatternAsync(string pattern)
        {
            // Placeholder: IDistributedCache has no pattern removal; this needs a Redis-specific implementation.
            _logger.LogInformation("Cache pattern invalidation requested (not implemented): {Pattern}", pattern);
            return Task.CompletedTask;
        }

        /// <summary>
        /// Clear all cache
        /// </summary>
        public Task ClearAllAsync()
        {
            // Placeholder: flushing needs a Redis-specific implementation.
            _logger.LogWarning("Clear-all requested (not implemented)");
            return Task.CompletedTask;
        }

        /// <summary>
        /// Get cache statistics
        /// </summary>
        public Task<CacheStatistics> GetStatisticsAsync()
        {
            // Placeholder: real statistics need a Redis-specific implementation.
            return Task.FromResult(new CacheStatistics
            {
                TotalKeys = 0,
                HitCount = 0,
                MissCount = 0,
                LastUpdated = DateTime.UtcNow
            });
        }
    }

    /// <summary>
    /// Cache options
    /// </summary>
    public class CacheOptions
    {
        public TimeSpan DefaultExpiration { get; set; } = TimeSpan.FromMinutes(30);
        public TimeSpan? SlidingExpiration { get; set; } = TimeSpan.FromMinutes(10);
        public string KeyPrefix { get; set; } = "bankcore:";
    }

    /// <summary>
    /// Cache statistics
    /// </summary>
    public class CacheStatistics
    {
        public long TotalKeys { get; set; }
        public long HitCount { get; set; }
        public long MissCount { get; set; }
        public DateTime LastUpdated { get; set; }
    }
}
