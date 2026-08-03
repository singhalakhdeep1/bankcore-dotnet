using System;
using System.Collections.Concurrent;
using System.Threading.Tasks;
using Microsoft.Extensions.Options;

namespace BankCore.Services.RateLimiting
{
    /// <summary>
    /// Token Bucket Rate Limiting Service
    /// Implements token bucket algorithm for API rate limiting
    /// </summary>
    public class RateLimitingService
    {
        private readonly ConcurrentDictionary<string, TokenBucket> _buckets = new();
        private readonly RateLimitOptions _options;
        private readonly ILogger<RateLimitingService> _logger;
        private readonly Timer _cleanupTimer;

        public RateLimitingService(
            IOptions<RateLimitOptions> options,
            ILogger<RateLimitingService> logger)
        {
            _options = options.Value;
            _logger = logger;
            
            // Start cleanup timer
            _cleanupTimer = new Timer(
                CleanupExpiredBuckets,
                null,
                TimeSpan.FromMinutes(5),
                TimeSpan.FromMinutes(5));
        }

        /// <summary>
        /// Check if request is allowed
        /// </summary>
        public async Task<bool> IsAllowedAsync(string key, int tokensRequested = 1)
        {
            var bucket = GetOrCreateBucket(key);
            
            lock (bucket)
            {
                var now = DateTime.UtcNow;
                var timeSinceLastRefill = (now - bucket.LastRefillTime).TotalSeconds;
                
                // Refill tokens based on time elapsed
                var tokensToAdd = (int)(timeSinceLastRefill * _options.RefillRate);
                bucket.Tokens = Math.Min(bucket.Tokens + tokensToAdd, _options.BucketCapacity);
                bucket.LastRefillTime = now;
                
                // Check if enough tokens available
                if (bucket.Tokens >= tokensRequested)
                {
                    bucket.Tokens -= tokensRequested;
                    bucket.LastRequestTime = now;
                    
                    _logger.LogDebug($"Request allowed for key: {key} (Tokens remaining: {bucket.Tokens})");
                    return true;
                }
                else
                {
                    _logger.LogWarning($"Request rate limited for key: {key} (Tokens needed: {tokensRequested}, Available: {bucket.Tokens})");
                    return false;
                }
            }
        }

        /// <summary>
        /// Get remaining tokens for a key
        /// </summary>
        public int GetRemainingTokens(string key)
        {
            var bucket = GetOrCreateBucket(key);
            
            lock (bucket)
            {
                var now = DateTime.UtcNow;
                var timeSinceLastRefill = (now - bucket.LastRefillTime).TotalSeconds;
                var tokensToAdd = (int)(timeSinceLastRefill * _options.RefillRate);
                bucket.Tokens = Math.Min(bucket.Tokens + tokensToAdd, _options.BucketCapacity);
                bucket.LastRefillTime = now;
                
                return bucket.Tokens;
            }
        }

        /// <summary>
        /// Get time until next token refill
        /// </summary>
        public TimeSpan GetTimeUntilRefill(string key)
        {
            var bucket = GetOrCreateBucket(key);
            
            lock (bucket)
            {
                if (bucket.Tokens < _options.BucketCapacity)
                {
                    var tokensNeeded = _options.BucketCapacity - bucket.Tokens;
                    var secondsToWait = tokensNeeded / _options.RefillRate;
                    return TimeSpan.FromSeconds(secondsToWait);
                }
                
                return TimeSpan.Zero;
            }
        }

        /// <summary>
        /// Reset bucket for a key
        /// </summary>
        public void ResetBucket(string key)
        {
            _buckets.TryRemove(key, out _);
            _logger.LogDebug($"Reset bucket for key: {key}");
        }

        /// <summary>
        /// Get or create token bucket
        /// </summary>
        private TokenBucket GetOrCreateBucket(string key)
        {
            return _buckets.GetOrAdd(key, _ => new TokenBucket
            {
                Tokens = _options.BucketCapacity,
                LastRefillTime = DateTime.UtcNow,
                LastRequestTime = DateTime.UtcNow,
                CreatedAt = DateTime.UtcNow
            });
        }

        /// <summary>
        /// Cleanup expired buckets
        /// </summary>
        private void CleanupExpiredBuckets(object? state)
        {
            var now = DateTime.UtcNow;
            var keysToRemove = new List<string>();

            foreach (var (key, bucket) in _buckets)
            {
                // Remove buckets that haven't been used for 1 hour
                if ((now - bucket.LastRequestTime).TotalHours > 1)
                {
                    keysToRemove.Add(key);
                }
            }

            foreach (var key in keysToRemove)
            {
                _buckets.TryRemove(key, out _);
                _logger.LogDebug($"Removed expired bucket: {key}");
            }

            _logger.LogInformation($"Rate limit bucket cleanup completed. Active buckets: {_buckets.Count}");
        }

        /// <summary>
        /// Get rate limiting statistics
        /// </summary>
        public RateLimitStatistics GetStatistics()
        {
            return new RateLimitStatistics
            {
                ActiveBuckets = _buckets.Count,
                BucketCapacity = _options.BucketCapacity,
                RefillRate = _options.RefillRate,
                LastCleanup = DateTime.UtcNow
            };
        }

        /// <summary>
        /// Clear all buckets
        /// </summary>
        public void ClearAllBuckets()
        {
            _buckets.Clear();
            _logger.LogWarning("Cleared all rate limit buckets");
        }
    }

    /// <summary>
    /// Token bucket for rate limiting
    /// </summary>
    public class TokenBucket
    {
        public int Tokens { get; set; }
        public DateTime LastRefillTime { get; set; }
        public DateTime LastRequestTime { get; set; }
        public DateTime CreatedAt { get; set; }
    }

    /// <summary>
    /// Rate limit options
    /// </summary>
    public class RateLimitOptions
    {
        public int BucketCapacity { get; set; } = 100;
        public double RefillRate { get; set; } = 1.0; // tokens per second
        public string KeyPrefix { get; set; } = "ratelimit:";
    }

    /// <summary>
    /// Rate limit statistics
    /// </summary>
    public class RateLimitStatistics
    {
        public int ActiveBuckets { get; set; }
        public int BucketCapacity { get; set; }
        public double RefillRate { get; set; }
        public DateTime LastCleanup { get; set; }
    }
}
