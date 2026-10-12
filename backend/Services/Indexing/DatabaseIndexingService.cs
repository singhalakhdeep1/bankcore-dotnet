using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using BankingAPI.Data;

namespace BankCore.Services.Indexing
{
    /// <summary>
    /// Database Indexing Service for Banking Queries
    /// Manages composite indexes for financial query optimization
    /// </summary>
    public class DatabaseIndexingService
    {
        private readonly ApplicationDbContext _context;
        private readonly ILogger<DatabaseIndexingService> _logger;

        public DatabaseIndexingService(ApplicationDbContext context, ILogger<DatabaseIndexingService> logger)
        {
            _context = context;
            _logger = logger;
        }

        /// <summary>
        /// Create all recommended indexes for banking operations
        /// </summary>
        public async Task CreateBankingIndexesAsync()
        {
            _logger.LogInformation("Creating banking database indexes...");

            var indexes = new List<DatabaseIndex>
            {
                // Account indexes
                new DatabaseIndex
                {
                    TableName = "Accounts",
                    IndexName = "IX_Accounts_AccountNumber",
                    Columns = new[] { "AccountNumber" },
                    IsUnique = true
                },
                new DatabaseIndex
                {
                    TableName = "Accounts",
                    IndexName = "IX_Accounts_UserId_AccountType",
                    Columns = new[] { "UserId", "AccountType" }
                },
                new DatabaseIndex
                {
                    TableName = "Accounts",
                    IndexName = "IX_Accounts_AccountType_Balance",
                    Columns = new[] { "AccountType", "Balance" }
                },

                // Transaction indexes
                new DatabaseIndex
                {
                    TableName = "Transactions",
                    IndexName = "IX_Transactions_AccountId_CreatedAt",
                    Columns = new[] { "AccountId", "CreatedAt" }
                },
                new DatabaseIndex
                {
                    TableName = "Transactions",
                    IndexName = "IX_Transactions_AccountId_Type_CreatedAt",
                    Columns = new[] { "AccountId", "Type", "CreatedAt" }
                },
                new DatabaseIndex
                {
                    TableName = "Transactions",
                    IndexName = "IX_Transactions_ReferenceNumber",
                    Columns = new[] { "ReferenceNumber" },
                    IsUnique = true
                },
                new DatabaseIndex
                {
                    TableName = "Transactions",
                    IndexName = "IX_Transactions_CreatedAt_Amount",
                    Columns = new[] { "CreatedAt", "Amount" }
                },

                // User indexes
                new DatabaseIndex
                {
                    TableName = "Users",
                    IndexName = "IX_Users_Email",
                    Columns = new[] { "Email" },
                    IsUnique = true
                },
                new DatabaseIndex
                {
                    TableName = "Users",
                    IndexName = "IX_Users_LastName_FirstName",
                    Columns = new[] { "LastName", "FirstName" }
                },

                // Payment indexes
                new DatabaseIndex
                {
                    TableName = "Payments",
                    IndexName = "IX_Payments_AccountId_Status_CreatedAt",
                    Columns = new[] { "AccountId", "Status", "CreatedAt" }
                },
                new DatabaseIndex
                {
                    TableName = "Payments",
                    IndexName = "IX_Payments_TransactionId",
                    Columns = new[] { "TransactionId" }
                },

                // Ledger indexes
                new DatabaseIndex
                {
                    TableName = "LedgerEntries",
                    IndexName = "IX_LedgerEntries_AccountId_Date",
                    Columns = new[] { "AccountId", "Date" }
                },
                new DatabaseIndex
                {
                    TableName = "LedgerEntries",
                    IndexName = "IX_LedgerEntries_Date_Type",
                    Columns = new[] { "Date", "Type" }
                }
            };

            foreach (var index in indexes)
            {
                await CreateIndexAsync(index);
            }

            _logger.LogInformation($"Created {indexes.Count} banking indexes");
        }

        /// <summary>
        /// Create a single index
        /// </summary>
        public async Task CreateIndexAsync(DatabaseIndex index)
        {
            try
            {
                var columns = string.Join(", ", index.Columns);
                var unique = index.IsUnique ? "UNIQUE " : "";
                var sql = $"CREATE {unique}INDEX IF NOT EXISTS \"{index.IndexName}\" ON \"{index.TableName}\" ({columns})";

                await _context.Database.ExecuteSqlRawAsync(sql);
                
                _logger.LogInformation($"Created index: {index.IndexName} on {index.TableName}");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, $"Failed to create index {index.IndexName}");
                throw;
            }
        }

        /// <summary>
        /// Drop an index
        /// </summary>
        public async Task DropIndexAsync(string indexName, string tableName)
        {
            try
            {
                var sql = $"DROP INDEX IF EXISTS \"{indexName}\"";
                await _context.Database.ExecuteSqlRawAsync(sql);
                
                _logger.LogInformation($"Dropped index: {indexName}");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, $"Failed to drop index {indexName}");
                throw;
            }
        }

        /// <summary>
        /// Get index usage statistics
        /// </summary>
        public async Task<List<IndexStatistics>> GetIndexStatisticsAsync()
        {
            var statistics = new List<IndexStatistics>();

            try
            {
                // PostgreSQL-specific query to get index statistics
                var sql = @"
                    SELECT 
                        schemaname,
                        tablename,
                        indexname,
                        idx_scan as index_scans,
                        idx_tup_read as tuples_read,
                        idx_tup_fetch as tuples_fetched
                    FROM pg_stat_user_indexes
                    ORDER BY idx_scan DESC";

                var result = await _context.Database.ExecuteSqlRawAsync(sql);
                
                _logger.LogInformation("Retrieved index statistics");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to retrieve index statistics");
            }

            return statistics;
        }

        /// <summary>
        /// Analyze query performance and suggest indexes
        /// </summary>
        public Task<List<IndexSuggestion>> AnalyzeQueryPerformanceAsync()
        {
            // Placeholder: a real implementation would read pg_stat_statements and suggest indexes.
            _logger.LogInformation("Query performance analysis is not implemented; returning no suggestions");
            return Task.FromResult(new List<IndexSuggestion>());
        }

        /// <summary>
        /// Rebuild all indexes
        /// </summary>
        public async Task RebuildIndexesAsync()
        {
            _logger.LogInformation("Rebuilding all indexes...");

            try
            {
                var sql = "REINDEX DATABASE CONCURRENTLY current_database()";
                await _context.Database.ExecuteSqlRawAsync(sql);
                
                _logger.LogInformation("Index rebuild completed");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to rebuild indexes");
                throw;
            }
        }

        /// <summary>
        /// Get all indexes on a table
        /// </summary>
        public async Task<List<DatabaseIndex>> GetTableIndexesAsync(string tableName)
        {
            var indexes = new List<DatabaseIndex>();

            try
            {
                var rows = await _context.Database
                    .SqlQuery<IndexRow>($"SELECT indexname AS \"Name\", indexdef AS \"Definition\" FROM pg_indexes WHERE tablename = {tableName}")
                    .ToListAsync();

                foreach (var row in rows)
                {
                    indexes.Add(new DatabaseIndex
                    {
                        TableName = tableName,
                        IndexName = row.Name,
                        IsUnique = row.Definition.Contains("UNIQUE INDEX", StringComparison.OrdinalIgnoreCase)
                    });
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to retrieve indexes for table {Table}", tableName);
            }

            return indexes;
        }

        private sealed class IndexRow
        {
            public string Name { get; set; } = string.Empty;
            public string Definition { get; set; } = string.Empty;
        }
    }

    /// <summary>
    /// Database index definition
    /// </summary>
    public class DatabaseIndex
    {
        public string TableName { get; set; } = string.Empty;
        public string IndexName { get; set; } = string.Empty;
        public string[] Columns { get; set; } = Array.Empty<string>();
        public bool IsUnique { get; set; }
        public bool IsClustered { get; set; }
    }

    /// <summary>
    /// Index statistics
    /// </summary>
    public class IndexStatistics
    {
        public string SchemaName { get; set; } = string.Empty;
        public string TableName { get; set; } = string.Empty;
        public string IndexName { get; set; } = string.Empty;
        public long IndexScans { get; set; }
        public long TuplesRead { get; set; }
        public long TuplesFetched { get; set; }
    }

    /// <summary>
    /// Index suggestion
    /// </summary>
    public class IndexSuggestion
    {
        public string TableName { get; set; } = string.Empty;
        public string[] Columns { get; set; } = Array.Empty<string>();
        public string Reason { get; set; } = string.Empty;
        public double EstimatedImprovement { get; set; }
    }
}
