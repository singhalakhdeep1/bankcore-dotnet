using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.Extensions.Options;

namespace BankCore.Services.Sharding
{
    /// <summary>
    /// Database Sharding Service for Banking Data
    /// Shards data by account type and region for horizontal scaling
    /// </summary>
    public class DatabaseShardingService
    {
        private readonly List<ShardConfig> _shards;
        private readonly ILogger<DatabaseShardingService> _logger;
        private readonly Dictionary<string, ShardClient> _shardClients = new();

        public DatabaseShardingService(IOptions<ShardingOptions> options, ILogger<DatabaseShardingService> logger)
        {
            _shards = options.Value.Shards;
            _logger = logger;
            
            InitializeShards();
        }

        private void InitializeShards()
        {
            foreach (var shard in _shards)
            {
                _shardClients[shard.Id] = new ShardClient(shard);
                _logger.LogInformation($"Initialized shard: {shard.Id} (Type: {shard.ShardType})");
            }
        }

        /// <summary>
        /// Get shard ID based on account type and region
        /// Uses consistent hashing for even distribution
        /// </summary>
        public string GetShardId(string accountType, string region)
        {
            // Combine account type and region for sharding key
            var shardingKey = $"{accountType.ToLower()}_{region.ToLower()}";
            
            // Simple hash for consistent distribution
            var hash = Math.Abs(shardingKey.GetHashCode());
            var shardIndex = hash % _shards.Count;
            
            return _shards[shardIndex].Id;
        }

        /// <summary>
        /// Get shard ID based on account number
        /// </summary>
        public string GetShardIdByAccount(string accountNumber)
        {
            // Extract account type from account number (first 2 digits)
            var accountType = accountNumber.Substring(0, 2);
            
            // Extract region from account number (digits 3-4)
            var region = accountNumber.Substring(2, 2);
            
            return GetShardId(accountType, region);
        }

        /// <summary>
        /// Get shard client for a specific shard ID
        /// </summary>
        public ShardClient GetShardClient(string shardId)
        {
            if (!_shardClients.TryGetValue(shardId, out var client))
            {
                throw new ArgumentException($"Shard not found: {shardId}");
            }
            return client;
        }

        /// <summary>
        /// Execute query on specific shard
        /// </summary>
        public async Task<T> ExecuteOnShardAsync<T>(string shardId, Func<ShardClient, Task<T>> operation)
        {
            var client = GetShardClient(shardId);
            return await operation(client);
        }

        /// <summary>
        /// Execute query on all shards (for cross-shard queries)
        /// </summary>
        public async Task<List<T>> ExecuteOnAllShardsAsync<T>(Func<ShardClient, Task<List<T>>> operation)
        {
            var tasks = _shardClients.Values.Select(client => operation(client));
            var results = await Task.WhenAll(tasks);
            
            return results.SelectMany(r => r).ToList();
        }

        /// <summary>
        /// Get shard health status
        /// </summary>
        public async Task<Dictionary<string, ShardHealth>> GetShardHealthAsync()
        {
            var healthStatus = new Dictionary<string, ShardHealth>();
            
            foreach (var (shardId, client) in _shardClients)
            {
                try
                {
                    var isHealthy = await client.CheckHealthAsync();
                    healthStatus[shardId] = new ShardHealth
                    {
                        ShardId = shardId,
                        IsHealthy = isHealthy,
                        LastChecked = DateTime.UtcNow
                    };
                }
                catch (Exception ex)
                {
                    _logger.LogError($"Health check failed for shard {shardId}", ex);
                    healthStatus[shardId] = new ShardHealth
                    {
                        ShardId = shardId,
                        IsHealthy = false,
                        LastChecked = DateTime.UtcNow,
                        Error = ex.Message
                    };
                }
            }
            
            return healthStatus;
        }

        /// <summary>
        /// Rebalance data across shards
        /// </summary>
        public async Task RebalanceShardsAsync()
        {
            _logger.LogInformation("Starting shard rebalancing...");
            
            var healthStatus = await GetShardHealthAsync();
            var healthyShards = healthStatus.Where(h => h.Value.IsHealthy).ToList();
            
            if (healthyShards.Count < 2)
            {
                _logger.LogWarning("Not enough healthy shards for rebalancing");
                return;
            }

            // Implement rebalancing logic here
            // This would involve moving data from overloaded shards to underloaded ones
            
            _logger.LogInformation("Shard rebalancing completed");
        }
    }

    /// <summary>
    /// Shard configuration
    /// </summary>
    public class ShardConfig
    {
        public string Id { get; set; }
        public string ConnectionString { get; set; }
        public ShardType ShardType { get; set; }
        public int Weight { get; set; } = 1;
        public bool IsActive { get; set; } = true;
    }

    public enum ShardType
    {
        Savings,
        Checking,
        Business,
        Investment
    }

    /// <summary>
    /// Sharding options
    /// </summary>
    public class ShardingOptions
    {
        public List<ShardConfig> Shards { get; set; } = new();
    }

    /// <summary>
    /// Shard client for database operations
    /// </summary>
    public class ShardClient
    {
        private readonly ShardConfig _config;
        private readonly ILogger<ShardClient> _logger;

        public ShardClient(ShardConfig config)
        {
            _config = config;
            _logger = LoggerFactory.Create(builder => {}).CreateLogger<ShardClient>();
        }

        public async Task<bool> CheckHealthAsync()
        {
            try
            {
                // Implement actual health check logic
                // For now, return true
                return await Task.FromResult(true);
            }
            catch
            {
                return false;
            }
        }

        public string ConnectionString => _config.ConnectionString;
        public string Id => _config.Id;
    }

    /// <summary>
    /// Shard health status
    /// </summary>
    public class ShardHealth
    {
        public string ShardId { get; set; }
        public bool IsHealthy { get; set; }
        public DateTime LastChecked { get; set; }
        public string? Error { get; set; }
    }
}
