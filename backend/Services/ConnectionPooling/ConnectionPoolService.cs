using System;
using System.Collections.Concurrent;
using System.Data;
using Microsoft.Extensions.Options;
using Npgsql;

namespace BankCore.Services.ConnectionPooling
{
    /// <summary>
    /// Connection Pooling Service for PostgreSQL
    /// Optimizes database connection management for high concurrency
    /// </summary>
    public class ConnectionPoolService : IDisposable
    {
        private readonly ConcurrentDictionary<string, NpgsqlConnection> _connections = new();
        private readonly ConnectionPoolOptions _options;
        private readonly ILogger<ConnectionPoolService> _logger;
        private readonly Timer _cleanupTimer;
        private bool _disposed;

        public ConnectionPoolService(
            IOptions<ConnectionPoolOptions> options,
            ILogger<ConnectionPoolService> logger)
        {
            _options = options.Value;
            _logger = logger;
            
            // Start cleanup timer to remove idle connections
            _cleanupTimer = new Timer(
                CleanupIdleConnections,
                null,
                TimeSpan.FromMinutes(5),
                TimeSpan.FromMinutes(5));
            
            _logger.LogInformation($"Connection pool initialized with max size: {_options.MaxPoolSize}");
        }

        /// <summary>
        /// Get connection from pool or create new one
        /// </summary>
        public async Task<NpgsqlConnection> GetConnectionAsync(string connectionString)
        {
            var connectionKey = GetConnectionKey(connectionString);
            
            // Try to get existing connection
            if (_connections.TryGetValue(connectionKey, out var connection))
            {
                if (IsConnectionValid(connection))
                {
                    _logger.LogDebug($"Reusing existing connection: {connectionKey}");
                    return connection;
                }
                else
                {
                    // Remove invalid connection
                    _connections.TryRemove(connectionKey, out _);
                    await connection.DisposeAsync();
                }
            }

            // Check pool size limit
            if (_connections.Count >= _options.MaxPoolSize)
            {
                _logger.LogWarning("Connection pool at maximum size, waiting for available connection");
                await WaitForAvailableConnectionAsync();
            }

            // Create new connection
            connection = await CreateConnectionAsync(connectionString);
            _connections[connectionKey] = connection;
            
            _logger.LogDebug($"Created new connection: {connectionKey} (Pool size: {_connections.Count})");
            return connection;
        }

        /// <summary>
        /// Return connection to pool
        /// </summary>
        public void ReturnConnection(NpgsqlConnection connection)
        {
            if (connection == null)
                return;

            var connectionKey = GetConnectionKey(connection.ConnectionString);
            
            if (IsConnectionValid(connection))
            {
                _logger.LogDebug($"Returned connection to pool: {connectionKey}");
                // Connection stays in pool for reuse
            }
            else
            {
                _connections.TryRemove(connectionKey, out _);
                connection.Dispose();
                _logger.LogDebug($"Removed invalid connection from pool: {connectionKey}");
            }
        }

        /// <summary>
        /// Create new connection with optimized settings
        /// </summary>
        private async Task<NpgsqlConnection> CreateConnectionAsync(string connectionString)
        {
            var builder = new NpgsqlConnectionStringBuilder(connectionString)
            {
                Pooling = false, // We manage our own pool
                MinPoolSize = 0,
                MaxPoolSize = 1,
                ConnectionLifetime = _options.ConnectionLifetime,
                CommandTimeout = _options.CommandTimeout,
                Keepalive = _options.KeepaliveInterval
            };

            var connection = new NpgsqlConnection(builder.ToString());
            await connection.OpenAsync();
            
            return connection;
        }

        /// <summary>
        /// Check if connection is valid
        /// </summary>
        private bool IsConnectionValid(NpgsqlConnection connection)
        {
            try
            {
                return connection != null && 
                       connection.State == ConnectionState.Open &&
                       !connection.IsBroken;
            }
            catch
            {
                return false;
            }
        }

        /// <summary>
        /// Wait for available connection (simple implementation)
        /// </summary>
        private async Task WaitForAvailableConnectionAsync()
        {
            var attempts = 0;
            while (_connections.Count >= _options.MaxPoolSize && attempts < _options.MaxWaitAttempts)
            {
                await Task.Delay(_options.WaitInterval);
                attempts++;
            }
            
            if (attempts >= _options.MaxWaitAttempts)
            {
                throw new TimeoutException("Timeout waiting for available database connection");
            }
        }

        /// <summary>
        /// Cleanup idle connections
        /// </summary>
        private void CleanupIdleConnections(object? state)
        {
            var keysToRemove = new List<string>();
            
            foreach (var (key, connection) in _connections)
            {
                if (!IsConnectionValid(connection))
                {
                    keysToRemove.Add(key);
                }
            }

            foreach (var key in keysToRemove)
            {
                if (_connections.TryRemove(key, out var connection))
                {
                    connection.Dispose();
                    _logger.LogDebug($"Cleaned up idle connection: {key}");
                }
            }

            _logger.LogInformation($"Connection pool cleanup completed. Current size: {_connections.Count}");
        }

        /// <summary>
        /// Get connection key
        /// </summary>
        private string GetConnectionKey(string connectionString)
        {
            var builder = new NpgsqlConnectionStringBuilder(connectionString);
            return $"{builder.Host}_{builder.Database}_{builder.Username}";
        }

        /// <summary>
        /// Get pool statistics
        /// </summary>
        public ConnectionPoolStatistics GetStatistics()
        {
            return new ConnectionPoolStatistics
            {
                TotalConnections = _connections.Count,
                MaxPoolSize = _options.MaxPoolSize,
                AvailableConnections = _options.MaxPoolSize - _connections.Count,
                LastCleanup = DateTime.UtcNow
            };
        }

        /// <summary>
        /// Clear all connections
        /// </summary>
        public async Task ClearAllConnectionsAsync()
        {
            _logger.LogWarning("Clearing all connections from pool");
            
            foreach (var (key, connection) in _connections)
            {
                await connection.DisposeAsync();
            }
            
            _connections.Clear();
        }

        public void Dispose()
        {
            Dispose(true);
            GC.SuppressFinalize(this);
        }

        protected virtual void Dispose(bool disposing)
        {
            if (!_disposed)
            {
                if (disposing)
                {
                    _cleanupTimer?.Dispose();
                    ClearAllConnectionsAsync().GetAwaiter().GetResult();
                }
                _disposed = true;
            }
        }
    }

    /// <summary>
    /// Connection pool options
    /// </summary>
    public class ConnectionPoolOptions
    {
        public int MaxPoolSize { get; set; } = 100;
        public int MaxWaitAttempts { get; set; } = 10;
        public TimeSpan WaitInterval { get; set; } = TimeSpan.FromMilliseconds(100);
        public TimeSpan ConnectionLifetime { get; set; } = TimeSpan.FromMinutes(30);
        public int CommandTimeout { get; set; } = 30;
        public int KeepaliveInterval { get; set; } = 60;
    }

    /// <summary>
    /// Connection pool statistics
    /// </summary>
    public class ConnectionPoolStatistics
    {
        public int TotalConnections { get; set; }
        public int MaxPoolSize { get; set; }
        public int AvailableConnections { get; set; }
        public DateTime LastCleanup { get; set; }
    }
}
