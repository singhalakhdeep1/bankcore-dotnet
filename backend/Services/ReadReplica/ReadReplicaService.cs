using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.Extensions.Options;

namespace BankCore.Services.ReadReplica
{
    /// <summary>
    /// Read Replica Service for Banking Analytics
    /// Routes read queries to replica databases for load distribution
    /// </summary>
    public class ReadReplicaService
    {
        private readonly List<ReplicaConfig> _replicas;
        private readonly string _primaryConnectionString;
        private readonly ILogger<ReadReplicaService> _logger;
        private readonly Dictionary<string, ReplicaClient> _replicaClients = new();
        private readonly Random _random = new();

        public ReadReplicaService(IOptions<ReadReplicaOptions> options, ILogger<ReadReplicaService> logger)
        {
            _replicas = options.Value.Replicas;
            _primaryConnectionString = options.Value.PrimaryConnectionString;
            _logger = logger;
            
            InitializeReplicas();
        }

        private void InitializeReplicas()
        {
            foreach (var replica in _replicas)
            {
                _replicaClients[replica.Id] = new ReplicaClient(replica);
                _logger.LogInformation($"Initialized replica: {replica.Id} (Region: {replica.Region})");
            }
        }

        /// <summary>
        /// Get a read replica for read operations
        /// Uses round-robin with health checks
        /// </summary>
        public ReplicaClient GetReadReplica()
        {
            var healthyReplicas = _replicaClients.Values
                .Where(r => r.IsHealthy)
                .ToList();

            if (healthyReplicas.Count == 0)
            {
                _logger.LogWarning("No healthy replicas available, falling back to primary");
                return new ReplicaClient(new ReplicaConfig
                {
                    Id = "primary",
                    ConnectionString = _primaryConnectionString,
                    Region = "primary",
                    IsPrimary = true
                });
            }

            // Simple round-robin selection
            var index = _random.Next(healthyReplicas.Count);
            return healthyReplicas[index];
        }

        /// <summary>
        /// Get replica by region for geo-distributed reads
        /// </summary>
        public ReplicaClient GetReplicaByRegion(string region)
        {
            var regionalReplicas = _replicaClients.Values
                .Where(r => r.IsHealthy && r.Region.Equals(region, StringComparison.OrdinalIgnoreCase))
                .ToList();

            if (regionalReplicas.Count > 0)
            {
                return regionalReplicas.First();
            }

            _logger.LogWarning($"No healthy replicas in region {region}, using any available replica");
            return GetReadReplica();
        }

        /// <summary>
        /// Execute read operation on replica
        /// </summary>
        public async Task<T> ExecuteReadAsync<T>(Func<ReplicaClient, Task<T>> operation)
        {
            var replica = GetReadReplica();
            return await operation(replica);
        }

        /// <summary>
        /// Execute read operation on specific region's replica
        /// </summary>
        public async Task<T> ExecuteReadInRegionAsync<T>(string region, Func<ReplicaClient, Task<T>> operation)
        {
            var replica = GetReplicaByRegion(region);
            return await operation(replica);
        }

        /// <summary>
        /// Execute write operation on primary
        /// </summary>
        public async Task<T> ExecuteWriteAsync<T>(Func<ReplicaClient, Task<T>> operation)
        {
            var primary = new ReplicaClient(new ReplicaConfig
            {
                Id = "primary",
                ConnectionString = _primaryConnectionString,
                Region = "primary",
                IsPrimary = true
            });
            
            return await operation(primary);
        }

        /// <summary>
        /// Check replica lag
        /// </summary>
        public async Task<Dictionary<string, ReplicaLag>> GetReplicaLagAsync()
        {
            var lagStatus = new Dictionary<string, ReplicaLag>();
            
            foreach (var (replicaId, client) in _replicaClients)
            {
                try
                {
                    var lag = await client.GetReplicationLagAsync();
                    lagStatus[replicaId] = new ReplicaLag
                    {
                        ReplicaId = replicaId,
                        LagSeconds = lag,
                        LastChecked = DateTime.UtcNow
                    };
                }
                catch (Exception ex)
                {
                    _logger.LogError($"Lag check failed for replica {replicaId}", ex);
                    lagStatus[replicaId] = new ReplicaLag
                    {
                        ReplicaId = replicaId,
                        LagSeconds = -1,
                        LastChecked = DateTime.UtcNow,
                        Error = ex.Message
                    };
                }
            }
            
            return lagStatus;
        }

        /// <summary>
        /// Health check for all replicas
        /// </summary>
        public async Task<Dictionary<string, bool>> CheckReplicaHealthAsync()
        {
            var healthStatus = new Dictionary<string, bool>();
            
            foreach (var (replicaId, client) in _replicaClients)
            {
                try
                {
                    var isHealthy = await client.CheckHealthAsync();
                    client.IsHealthy = isHealthy;
                    healthStatus[replicaId] = isHealthy;
                    
                    if (!isHealthy)
                    {
                        _logger.LogWarning($"Replica {replicaId} is unhealthy");
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogError($"Health check failed for replica {replicaId}", ex);
                    client.IsHealthy = false;
                    healthStatus[replicaId] = false;
                }
            }
            
            return healthStatus;
        }
    }

    /// <summary>
    /// Replica configuration
    /// </summary>
    public class ReplicaConfig
    {
        public string Id { get; set; }
        public string ConnectionString { get; set; }
        public string Region { get; set; }
        public bool IsPrimary { get; set; }
        public int Weight { get; set; } = 1;
    }

    /// <summary>
    /// Read replica options
    /// </summary>
    public class ReadReplicaOptions
    {
        public string PrimaryConnectionString { get; set; }
        public List<ReplicaConfig> Replicas { get; set; } = new();
    }

    /// <summary>
    /// Replica client for database operations
    /// </summary>
    public class ReplicaClient
    {
        private readonly ReplicaConfig _config;
        private readonly ILogger<ReplicaClient> _logger;

        public ReplicaClient(ReplicaConfig config)
        {
            _config = config;
            _logger = LoggerFactory.Create(builder => {}).CreateLogger<ReplicaClient>();
            IsHealthy = true;
        }

        public bool IsHealthy { get; set; }
        public string Region => _config.Region;
        public string Id => _config.Id;
        public bool IsPrimary => _config.IsPrimary;

        public async Task<bool> CheckHealthAsync()
        {
            try
            {
                // Implement actual health check logic
                return await Task.FromResult(true);
            }
            catch
            {
                return false;
            }
        }

        public async Task<int> GetReplicationLagAsync()
        {
            // Implement actual replication lag check
            // For now, return 0
            return await Task.FromResult(0);
        }

        public string ConnectionString => _config.ConnectionString;
    }

    /// <summary>
    /// Replication lag status
    /// </summary>
    public class ReplicaLag
    {
        public string ReplicaId { get; set; }
        public int LagSeconds { get; set; }
        public DateTime LastChecked { get; set; }
        public string? Error { get; set; }
    }
}
