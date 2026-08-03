using System;
using System.Collections.Generic;

namespace BankCore.Services.CapacityEstimation
{
    /// <summary>
    /// Capacity Estimation Service for Banking Transactions
    /// Calculates resource requirements for scaling banking operations
    /// </summary>
    public class CapacityEstimationService
    {
        private readonly ILogger<CapacityEstimationService> _logger;
        private readonly CapacityOptions _options;

        public CapacityEstimationService(
            IOptions<CapacityOptions> options,
            ILogger<CapacityEstimationService> logger)
        {
            _options = options.Value;
            _logger = logger;
        }

        /// <summary>
        /// Estimate database capacity requirements
        /// </summary>
        public DatabaseCapacity EstimateDatabaseCapacity(TransactionVolume volume)
        {
            var transactionsPerDay = volume.DailyTransactions;
            var transactionsPerMonth = transactionsPerDay * 30;
            var transactionsPerYear = transactionsPerDay * 365;

            // Average transaction record size (estimated)
            var avgRecordSize = 500; // bytes
            var indexOverhead = 0.3; // 30% overhead for indexes

            var dailyStorage = transactionsPerDay * avgRecordSize * (1 + indexOverhead);
            var monthlyStorage = dailyStorage * 30;
            var yearlyStorage = dailyStorage * 365;

            // Account records storage
            var accountCount = volume.AccountCount;
            var avgAccountRecordSize = 1000; // bytes
            var accountStorage = accountCount * avgAccountRecordSize * (1 + indexOverhead);

            // User records storage
            var userCount = volume.UserCount;
            var avgUserRecordSize = 2000; // bytes
            var userStorage = userCount * avgUserRecordSize * (1 + indexOverhead);

            var totalStorage = yearlyStorage + accountStorage + userStorage;

            // IOPS estimation
            var readWriteRatio = 0.7; // 70% reads, 30% writes
            var avgIOPSPerTransaction = 5;
            var peakIOPS = (int)(transactionsPerDay / 86400 * avgIOPSPerTransaction * 2); // 2x for peak

            return new DatabaseCapacity
            {
                DailyTransactions = transactionsPerDay,
                MonthlyTransactions = transactionsPerMonth,
                YearlyTransactions = transactionsPerYear,
                DailyStorageBytes = dailyStorage,
                MonthlyStorageBytes = monthlyStorage,
                YearlyStorageBytes = yearlyStorage,
                AccountStorageBytes = accountStorage,
                UserStorageBytes = userStorage,
                TotalStorageBytes = totalStorage,
                TotalStorageGB = totalStorage / (1024 * 1024 * 1024),
                TotalStorageTB = totalStorage / (1024L * 1024 * 1024 * 1024),
                EstimatedIOPS = peakIOPS,
                ReadWriteRatio = readWriteRatio
            };
        }

        /// <summary>
        /// Estimate application server capacity
        /// </summary>
        public ApplicationCapacity EstimateApplicationCapacity(TransactionVolume volume)
        {
            var transactionsPerSecond = volume.DailyTransactions / 86400;
            var peakTPS = transactionsPerSecond * 3; // 3x peak factor

            // CPU requirements
            var cpuCoresPerTPS = 0.1; // 0.1 cores per TPS
            var totalCPUCores = (int)(peakTPS * cpuCoresPerTPS);
            var serverCount = (int)Math.Ceiling(totalCPUCores / _options.CoresPerServer);

            // Memory requirements
            var memoryPerServerGB = _options.MemoryPerServerGB;
            var totalMemoryGB = serverCount * memoryPerServerGB;

            // Network bandwidth
            var avgRequestSize = 1024; // 1 KB
            var avgResponseSize = 2048; // 2 KB
            var bandwidthPerSecond = peakTPS * (avgRequestSize + avgResponseSize);
            var bandwidthMbps = bandwidthPerSecond * 8 / 1_000_000;

            return new ApplicationCapacity
            {
                TransactionsPerSecond = transactionsPerSecond,
                PeakTPS = peakTPS,
                TotalCPUCores = totalCPUCores,
                ServerCount = serverCount,
                CoresPerServer = _options.CoresPerServer,
                TotalMemoryGB = totalMemoryGB,
                MemoryPerServerGB = memoryPerServerGB,
                BandwidthMbps = bandwidthMbps,
                RecommendedInstanceType = GetRecommendedInstanceType(totalCPUCores, memoryPerServerGB)
            };
        }

        /// <summary>
        /// Estimate cache capacity
        /// </summary>
        public CacheCapacity EstimateCacheCapacity(TransactionVolume volume)
        {
            var accountCount = volume.AccountCount;
            var avgAccountCacheSize = 1024; // 1 KB per account
            var accountCacheSize = accountCount * avgAccountCacheSize;

            var transactionCacheSize = volume.DailyTransactions * 100; // 100 bytes per transaction cache

            var sessionCacheSize = volume.UserCount * 512; // 512 bytes per session

            var totalCacheSize = accountCacheSize + transactionCacheSize + sessionCacheSize;

            // Redis memory overhead
            var memoryOverhead = 1.5; // 50% overhead
            var totalMemoryRequired = totalCacheSize * memoryOverhead;

            var redisNodes = (int)Math.Ceiling(totalMemoryRequired / (_options.RedisMemoryPerNodeGB * 1024 * 1024 * 1024));

            return new CacheCapacity
            {
                AccountCacheSizeBytes = accountCacheSize,
                TransactionCacheSizeBytes = transactionCacheSize,
                SessionCacheSizeBytes = sessionCacheSize,
                TotalCacheSizeBytes = totalCacheSize,
                TotalMemoryRequiredBytes = totalMemoryRequired,
                TotalMemoryRequiredGB = totalMemoryRequired / (1024 * 1024 * 1024),
                RedisNodes = redisNodes,
                RedisMemoryPerNodeGB = _options.RedisMemoryPerNodeGB
            };
        }

        /// <summary>
        /// Generate comprehensive capacity report
        /// </summary>
        public CapacityReport GenerateCapacityReport(TransactionVolume volume)
        {
            var dbCapacity = EstimateDatabaseCapacity(volume);
            var appCapacity = EstimateApplicationCapacity(volume);
            var cacheCapacity = EstimateCacheCapacity(volume);

            return new CapacityReport
            {
                TransactionVolume = volume,
                DatabaseCapacity = dbCapacity,
                ApplicationCapacity = appCapacity,
                CacheCapacity = cacheCapacity,
                GeneratedAt = DateTime.UtcNow,
                Recommendations = GenerateRecommendations(dbCapacity, appCapacity, cacheCapacity)
            };
        }

        /// <summary>
        /// Generate recommendations based on capacity analysis
        /// </summary>
        private List<string> GenerateRecommendations(
            DatabaseCapacity dbCapacity,
            ApplicationCapacity appCapacity,
            CacheCapacity cacheCapacity)
        {
            var recommendations = new List<string>();

            // Database recommendations
            if (dbCapacity.TotalStorageTB > 1)
            {
                recommendations.Add("Consider database sharding for storage > 1TB");
            }

            if (dbCapacity.EstimatedIOPS > 10000)
            {
                recommendations.Add("Use provisioned IOPS storage for high IOPS requirements");
            }

            // Application recommendations
            if (appCapacity.ServerCount > 10)
            {
                recommendations.Add("Consider using Kubernetes for auto-scaling");
            }

            if (appCapacity.BandwidthMbps > 1000)
            {
                recommendations.Add("Use CDN for static content to reduce bandwidth");
            }

            // Cache recommendations
            if (cacheCapacity.RedisNodes > 3)
            {
                recommendations.Add("Use Redis Cluster for distributed caching");
            }

            // General recommendations
            recommendations.Add("Implement read replicas for read-heavy workloads");
            recommendations.Add("Use connection pooling for database connections");
            recommendations.Add("Implement rate limiting to prevent abuse");

            return recommendations;
        }

        /// <summary>
        /// Get recommended AWS instance type
        /// </summary>
        private string GetRecommendedInstanceType(int cores, int memoryGB)
        {
            return (cores, memoryGB) switch
            {
                (<= 2, <= 4) => "t3.medium",
                (<= 4, <= 8) => "t3.large",
                (<= 8, <= 16) => "m5.xlarge",
                (<= 16, <= 32) => "m5.2xlarge",
                (<= 32, <= 64) => "m5.4xlarge",
                _ => "m5.8xlarge"
            };
        }
    }

    /// <summary>
    /// Transaction volume for capacity planning
    /// </summary>
    public class TransactionVolume
    {
        public int DailyTransactions { get; set; }
        public int AccountCount { get; set; }
        public int UserCount { get; set; }
        public int PeakHourTransactions { get; set; }
    }

    /// <summary>
    /// Database capacity estimation
    /// </summary>
    public class DatabaseCapacity
    {
        public long DailyTransactions { get; set; }
        public long MonthlyTransactions { get; set; }
        public long YearlyTransactions { get; set; }
        public double DailyStorageBytes { get; set; }
        public double MonthlyStorageBytes { get; set; }
        public double YearlyStorageBytes { get; set; }
        public double AccountStorageBytes { get; set; }
        public double UserStorageBytes { get; set; }
        public double TotalStorageBytes { get; set; }
        public double TotalStorageGB { get; set; }
        public double TotalStorageTB { get; set; }
        public int EstimatedIOPS { get; set; }
        public double ReadWriteRatio { get; set; }
    }

    /// <summary>
    /// Application capacity estimation
    /// </summary>
    public class ApplicationCapacity
    {
        public double TransactionsPerSecond { get; set; }
        public double PeakTPS { get; set; }
        public int TotalCPUCores { get; set; }
        public int ServerCount { get; set; }
        public int CoresPerServer { get; set; }
        public int TotalMemoryGB { get; set; }
        public int MemoryPerServerGB { get; set; }
        public double BandwidthMbps { get; set; }
        public string RecommendedInstanceType { get; set; }
    }

    /// <summary>
    /// Cache capacity estimation
    /// </summary>
    public class CacheCapacity
    {
        public double AccountCacheSizeBytes { get; set; }
        public double TransactionCacheSizeBytes { get; set; }
        public double SessionCacheSizeBytes { get; set; }
        public double TotalCacheSizeBytes { get; set; }
        public double TotalMemoryRequiredBytes { get; set; }
        public double TotalMemoryRequiredGB { get; set; }
        public int RedisNodes { get; set; }
        public int RedisMemoryPerNodeGB { get; set; }
    }

    /// <summary>
    /// Comprehensive capacity report
    /// </summary>
    public class CapacityReport
    {
        public TransactionVolume TransactionVolume { get; set; }
        public DatabaseCapacity DatabaseCapacity { get; set; }
        public ApplicationCapacity ApplicationCapacity { get; set; }
        public CacheCapacity CacheCapacity { get; set; }
        public DateTime GeneratedAt { get; set; }
        public List<string> Recommendations { get; set; }
    }

    /// <summary>
    /// Capacity estimation options
    /// </summary>
    public class CapacityOptions
    {
        public int CoresPerServer { get; set; } = 4;
        public int MemoryPerServerGB { get; set; } = 16;
        public int RedisMemoryPerNodeGB { get; set; } = 8;
    }
}
