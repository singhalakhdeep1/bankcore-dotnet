using System;
using System.Collections.Concurrent;
using System.Threading.Tasks;
using Microsoft.Extensions.Caching.Distributed;

namespace BankCore.Services.Idempotency
{
    /// <summary>
    /// Idempotency Service for Banking Transactions
    /// Ensures duplicate requests don't cause duplicate transactions
    /// </summary>
    public class IdempotencyService
    {
        private readonly IDistributedCache _cache;
        private readonly ILogger<IdempotencyService> _logger;
        private readonly IdempotencyOptions _options;

        public IdempotencyService(
            IDistributedCache cache,
            IOptions<IdempotencyOptions> options,
            ILogger<IdempotencyService> logger)
        {
            _cache = cache;
            _options = options.Value;
            _logger = logger;
        }

        /// <summary>
        /// Check if request with idempotency key has been processed
        /// </summary>
        public async Task<bool> IsProcessedAsync(string idempotencyKey)
        {
            try
            {
                var cacheKey = GetCacheKey(idempotencyKey);
                var cachedResult = await _cache.GetStringAsync(cacheKey);
                
                var isProcessed = !string.IsNullOrEmpty(cachedResult);
                
                if (isProcessed)
                {
                    _logger.LogDebug($"Idempotency key already processed: {idempotencyKey}");
                }
                
                return isProcessed;
            }
            catch (Exception ex)
            {
                _logger.LogError($"Error checking idempotency for key: {idempotencyKey}", ex);
                // Fail open - allow request if cache check fails
                return false;
            }
        }

        /// <summary>
        /// Get cached response for idempotency key
        /// </summary>
        public async Task<T?> GetCachedResponseAsync<T>(string idempotencyKey)
        {
            try
            {
                var cacheKey = GetCacheKey(idempotencyKey);
                var cachedValue = await _cache.GetStringAsync(cacheKey);
                
                if (string.IsNullOrEmpty(cachedValue))
                {
                    return default;
                }

                return System.Text.Json.JsonSerializer.Deserialize<T>(cachedValue);
            }
            catch (Exception ex)
            {
                _logger.LogError($"Error getting cached response for key: {idempotencyKey}", ex);
                return default;
            }
        }

        /// <summary>
        /// Store response for idempotency key
        /// </summary>
        public async Task StoreResponseAsync<T>(string idempotencyKey, T response)
        {
            try
            {
                var cacheKey = GetCacheKey(idempotencyKey);
                var serializedValue = System.Text.Json.JsonSerializer.Serialize(response);
                
                var options = new Microsoft.Extensions.Caching.Distributed.DistributedCacheEntryOptions
                {
                    AbsoluteExpirationRelativeToNow = _options.Expiration
                };

                await _cache.SetStringAsync(cacheKey, serializedValue, options);
                
                _logger.LogDebug($"Stored response for idempotency key: {idempotencyKey}");
            }
            catch (Exception ex)
            {
                _logger.LogError($"Error storing response for idempotency key: {idempotencyKey}", ex);
            }
        }

        /// <summary>
        /// Execute operation with idempotency check
        /// </summary>
        public async Task<T> ExecuteWithIdempotencyAsync<T>(
            string idempotencyKey,
            Func<Task<T>> operation)
        {
            // Check if already processed
            if (await IsProcessedAsync(idempotencyKey))
            {
                var cachedResponse = await GetCachedResponseAsync<T>(idempotencyKey);
                if (cachedResponse != null)
                {
                    return cachedResponse;
                }
            }

            // Execute operation
            var result = await operation();

            // Store result
            await StoreResponseAsync(idempotencyKey, result);

            return result;
        }

        /// <summary>
        /// Validate idempotency key format
        /// </summary>
        public bool IsValidIdempotencyKey(string key)
        {
            if (string.IsNullOrEmpty(key))
                return false;

            // Check if key is a valid UUID
            return Guid.TryParse(key, out _);
        }

        /// <summary>
        /// Generate idempotency key if not provided
        /// </summary>
        public string GenerateIdempotencyKey()
        {
            return Guid.NewGuid().ToString();
        }

        /// <summary>
        /// Invalidate idempotency key
        /// </summary>
        public async Task InvalidateAsync(string idempotencyKey)
        {
            try
            {
                var cacheKey = GetCacheKey(idempotencyKey);
                await _cache.RemoveAsync(cacheKey);
                
                _logger.LogDebug($"Invalidated idempotency key: {idempotencyKey}");
            }
            catch (Exception ex)
            {
                _logger.LogError($"Error invalidating idempotency key: {idempotencyKey}", ex);
            }
        }

        /// <summary>
        /// Get cache key for idempotency
        /// </summary>
        private string GetCacheKey(string idempotencyKey)
        {
            return $"{_options.KeyPrefix}{idempotencyKey}";
        }

        /// <summary>
        /// Get idempotency statistics
        /// </summary>
        public async Task<IdempotencyStatistics> GetStatisticsAsync()
        {
            // This would require Redis-specific implementation to get statistics
            return new IdempotencyStatistics
            {
                TotalKeys = 0,
                LastUpdated = DateTime.UtcNow
            };
        }
    }

    /// <summary>
    /// Idempotency options
    /// </summary>
    public class IdempotencyOptions
    {
        public TimeSpan Expiration { get; set; } = TimeSpan.FromHours(24);
        public string KeyPrefix { get; set; } = "idempotency:";
    }

    /// <summary>
    /// Idempotency statistics
    /// </summary>
    public class IdempotencyStatistics
    {
        public long TotalKeys { get; set; }
        public DateTime LastUpdated { get; set; }
    }
}
