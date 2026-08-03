# BankCore .NET - HLD/LLD Implementation Guide

## Overview
This document provides comprehensive documentation for all High-Level Design (HLD) and Low-Level Design (LLD) patterns implemented in the BankCore .NET banking application.

## Implemented Topics

### HLD Topics (High-Level Design)

#### 1. Saga Pattern
**Location:** `Services/Saga/SagaManager.cs`

**Description:** Implements the Saga pattern for managing distributed transactions with compensation logic. Used for complex transaction workflows that span multiple services.

**Key Features:**
- Execute saga with compensation steps
- Automatic rollback on failure
- Execution tracking and monitoring
- Support for retryable steps

**Usage Example:**
```csharp
var saga = new SagaDefinition<TransactionContext>
{
    Name = "PaymentProcessing",
    Steps = new List<SagaStep<TransactionContext>>
    {
        new SagaStep<TransactionContext>
        {
            Name = "ValidatePayment",
            Execute = async (ctx) => await ValidatePayment(ctx),
            Compensate = async (ctx) => await InvalidatePayment(ctx)
        },
        new SagaStep<TransactionContext>
        {
            Name = "ProcessPayment",
            Execute = async (ctx) => await ProcessPayment(ctx),
            Compensate = async (ctx) => await RefundPayment(ctx)
        }
    }
};

var result = await _sagaManager.ExecuteAsync(saga, context);
```

#### 2. Database Sharding
**Location:** `Services/Sharding/DatabaseShardingService.cs`

**Description:** Implements database sharding by account type and region for horizontal scaling. Uses consistent hashing for even distribution.

**Key Features:**
- Shard by account type (Savings, Checking, Business, Investment)
- Shard by region for geo-distribution
- Consistent hashing for even distribution
- Shard health monitoring
- Automatic rebalancing

**Usage Example:**
```csharp
// Get shard for account
var shardId = _shardingService.GetShardIdByAccount(accountNumber);

// Execute on specific shard
var result = await _shardingService.ExecuteOnShardAsync(shardId, async (client) => 
{
    return await client.QueryAccount(accountNumber);
});

// Execute on all shards
var results = await _shardingService.ExecuteOnAllShardsAsync(async (client) => 
{
    return await client.GetAllAccounts();
});
```

#### 3. Read Replicas
**Location:** `Services/ReadReplica/ReadReplicaService.cs`

**Description:** Implements read replica routing for load distribution. Routes read queries to replica databases while writes go to primary.

**Key Features:**
- Round-robin replica selection
- Region-aware routing
- Replication lag monitoring
- Automatic failover to primary
- Health checking

**Usage Example:**
```csharp
// Execute read on replica
var result = await _readReplicaService.ExecuteReadAsync(async (replica) => 
{
    return await replica.QueryAccount(accountId);
});

// Execute read in specific region
var result = await _readReplicaService.ExecuteReadInRegionAsync("us-east-1", async (replica) => 
{
    return await replica.QueryAccount(accountId);
});

// Execute write on primary
await _readReplicaService.ExecuteWriteAsync(async (primary) => 
{
    return await primary.UpdateAccount(accountId, data);
});
```

#### 4. Distributed Caching Coherency
**Location:** `Services/Caching/DistributedCacheService.cs`

**Description:** Implements distributed caching with cache-aside pattern. Manages cache coherency across multiple instances.

**Key Features:**
- Cache-aside pattern
- Distributed cache with Redis
- Automatic expiration
- Sliding expiration support
- Pattern-based invalidation

**Usage Example:**
```csharp
// Get from cache
var account = await _cacheService.GetAsync<Account>($"account:{accountId}");

// Set in cache
await _cacheService.SetAsync($"account:{accountId}", account, TimeSpan.FromMinutes(30));

// Get or set pattern
var account = await _cacheService.GetOrSetAsync($"account:{accountId}", async () => 
{
    return await _accountService.GetAccount(accountId);
}, TimeSpan.FromMinutes(30));
```

#### 5. API Gateway Security
**Location:** Implemented via JWT authentication and rate limiting middleware

**Description:** JWT validation at API gateway level with rate limiting for security.

**Key Features:**
- JWT token validation
- Rate limiting per endpoint
- IP-based throttling
- Request authentication

#### 6. Capacity Estimation
**Location:** `Services/CapacityEstimation/CapacityEstimationService.cs`

**Description:** Calculates resource requirements for scaling banking operations based on transaction volume.

**Key Features:**
- Database capacity estimation
- Application server capacity estimation
- Cache capacity estimation
- Comprehensive capacity reports
- AWS instance type recommendations

**Usage Example:**
```csharp
var volume = new TransactionVolume
{
    DailyTransactions = 100000,
    AccountCount = 50000,
    UserCount = 100000
};

var report = _capacityService.GenerateCapacityReport(volume);

Console.WriteLine($"Total Storage: {report.DatabaseCapacity.TotalStorageTB} TB");
Console.WriteLine($"Server Count: {report.ApplicationCapacity.ServerCount}");
Console.WriteLine($"Redis Nodes: {report.CacheCapacity.RedisNodes}");
```

#### 7. System Constraints Analysis
**Location:** `Services/SystemConstraints/SystemConstraintsAnalysisService.cs`

**Description:** Analyzes system constraints and compliance requirements for banking operations.

**Key Features:**
- Latency constraints analysis
- Throughput constraints analysis
- Availability constraints analysis
- Security constraints analysis
- Compliance constraints analysis
- Data constraints analysis
- Compliance scoring

**Usage Example:**
```csharp
var requirements = new SystemRequirements
{
    TargetLatencyMs = 200,
    CurrentLatencyMs = 150,
    TargetTPS = 1000,
    CurrentTPS = 800,
    TargetUptime = 99.9,
    CurrentUptime = 99.95
};

var analysis = _constraintsService.AnalyzeConstraints(requirements);
var report = _constraintsService.GenerateComplianceReport(requirements);

Console.WriteLine($"Compliance Score: {report.ComplianceScore}/100");
```

### LLD Topics (Low-Level Design)

#### 1. Repository Pattern
**Location:** `Services/Repositories/IRepository.cs`, `Services/Repositories/Repository.cs`

**Description:** Generic repository implementation for data access abstraction with Unit of Work support.

**Key Features:**
- Generic repository interface
- CRUD operations
- Query with filtering and ordering
- Include related entities
- Pagination support
- Cursor-based pagination

**Usage Example:**
```csharp
// Get repository
var accountRepo = _unitOfWork.Repository<Account>();

// Get by ID
var account = await accountRepo.GetByIdAsync(accountId);

// Find with filter
var accounts = await accountRepo.FindAsync(a => a.Balance > 1000);

// Get with includes
var account = await accountRepo.GetByIdAsync(accountId, a => a.Transactions, a => a.User);

// Paginated results
var paged = await _unitOfWork.PaginatedRepository<Account>().GetPagedAsync(1, 20);
```

#### 2. Unit of Work Pattern
**Location:** `Services/UnitOfWork/IUnitOfWork.cs`

**Description:** Manages transactions and repository coordination. Ensures atomic operations across multiple repositories.

**Key Features:**
- Transaction management
- Repository coordination
- Automatic rollback on failure
- Commit/rollback operations

**Usage Example:**
```csharp
await _unitOfWork.BeginTransactionAsync();

try
{
    var accountRepo = _unitOfWork.Repository<Account>();
    var transactionRepo = _unitOfWork.Repository<Transaction>();

    var account = await accountRepo.GetByIdAsync(accountId);
    account.Balance -= amount;

    var transaction = new Transaction { Amount = amount, AccountId = accountId };
    
    await accountRepo.UpdateAsync(account);
    await transactionRepo.AddAsync(transaction);

    await _unitOfWork.CommitAsync();
}
catch
{
    await _unitOfWork.RollbackAsync();
    throw;
}
```

#### 3. Connection Pooling
**Location:** `Services/ConnectionPooling/ConnectionPoolService.cs`

**Description:** Optimizes database connection management for high concurrency with custom connection pool.

**Key Features:**
- Custom connection pool
- Connection reuse
- Pool size management
- Idle connection cleanup
- Health checking
- Statistics tracking

**Usage Example:**
```csharp
// Get connection from pool
var connection = await _connectionPool.GetConnectionAsync(connectionString);

// Use connection
using (var command = connection.CreateCommand())
{
    command.CommandText = "SELECT * FROM accounts";
    // Execute query
}

// Return connection to pool
_connectionPool.ReturnConnection(connection);

// Get pool statistics
var stats = _connectionPool.GetStatistics();
```

#### 4. Database Indexing
**Location:** `Services/Indexing/DatabaseIndexingService.cs`

**Description:** Manages composite indexes for financial query optimization. Creates optimized indexes for banking operations.

**Key Features:**
- Composite index creation
- Index management
- Index usage statistics
- Query performance analysis
- Index suggestions
- Index rebuilding

**Usage Example:**
```csharp
// Create all banking indexes
await _indexingService.CreateBankingIndexesAsync();

// Create specific index
var index = new DatabaseIndex
{
    TableName = "Accounts",
    IndexName = "IX_Accounts_UserId_AccountType",
    Columns = new[] { "UserId", "AccountType" }
};
await _indexingService.CreateIndexAsync(index);

// Get index statistics
var stats = await _indexingService.GetIndexStatisticsAsync();
```

#### 5. API Rate Limiting
**Location:** `Services/RateLimiting/RateLimitingService.cs`

**Description:** Token bucket algorithm for API rate limiting. Prevents abuse and ensures fair usage.

**Key Features:**
- Token bucket algorithm
- Per-key rate limiting
- Configurable bucket capacity
- Configurable refill rate
- Statistics tracking
- Automatic cleanup

**Usage Example:**
```csharp
// Check if request is allowed
var key = $"user:{userId}";
if (await _rateLimitService.IsAllowedAsync(key))
{
    // Process request
}
else
{
    // Return 429 Too Many Requests
}

// Get remaining tokens
var remaining = _rateLimitService.GetRemainingTokens(key);

// Get time until refill
var timeToRefill = _rateLimitService.GetTimeUntilRefill(key);
```

#### 6. Idempotency Patterns
**Location:** `Services/Idempotency/IdempotencyService.cs`

**Description:** Ensures duplicate requests don't cause duplicate transactions. Uses idempotency keys for deduplication.

**Key Features:**
- Idempotency key validation
- Response caching
- UUID key generation
- Automatic expiration
- Pattern-based invalidation

**Usage Example:**
```csharp
// Execute with idempotency
var idempotencyKey = request.Headers["Idempotency-Key"];
var result = await _idempotencyService.ExecuteWithIdempotencyAsync(idempotencyKey, async () => 
{
    return await _transactionService.ProcessTransaction(request);
});

// Validate idempotency key
if (!_idempotencyService.IsValidIdempotencyKey(key))
{
    return BadRequest("Invalid idempotency key");
}

// Generate idempotency key
var key = _idempotencyService.GenerateIdempotencyKey();
```

#### 7. Structured Logging
**Location:** `Services/Logging/StructuredLoggingService.cs`

**Description:** Implements structured logging with correlation IDs and context for financial audit logs.

**Key Features:**
- Structured log entries
- Correlation ID tracking
- User context tracking
- Audit event logging
- Transaction event logging
- Exception information

**Usage Example:**
```csharp
// Set log context
_loggingService.SetContext(new LogContext
{
    CorrelationId = StructuredLoggingService.CreateCorrelationId(),
    UserId = userId,
    AccountId = accountId,
    IpAddress = HttpContext.Connection.RemoteIpAddress?.ToString()
});

// Log information
_loggingService.LogInformation("Account balance checked");

// Log audit event
_loggingService.LogAuditEvent(AuditEventType.TransactionCompleted, "Payment processed");

// Log transaction event
_loggingService.LogTransactionEvent(transactionId, TransactionEventType.Completed, "Transaction completed");
```

#### 8. Error Handling Patterns
**Location:** `Services/ErrorHandling/ErrorHandlingService.cs`

**Description:** Implements resilience patterns including retry, circuit breaker, and timeout for external service calls.

**Key Features:**
- Retry with exponential backoff
- Circuit breaker pattern
- Timeout handling
- Fallback strategy
- Policy composition
- Circuit breaker state management

**Usage Example:**
```csharp
// Execute with retry
var result = await _errorHandlingService.ExecuteWithRetryAsync(async () => 
{
    return await _externalService.CallApi();
});

// Execute with circuit breaker
var result = await _errorHandlingService.ExecuteWithCircuitBreakerAsync(async () => 
{
    return await _externalService.CallApi();
});

// Execute with all resilience patterns
var result = await _errorHandlingService.ExecuteWithResilienceAsync(async () => 
{
    return await _externalService.CallApi();
});

// Get circuit breaker state
var state = _errorHandlingService.GetCircuitBreakerState();
```

#### 9. GoF Patterns

##### Strategy Pattern (Payment Providers)
**Location:** `Services/Patterns/PaymentStrategy.cs`

**Description:** Strategy pattern for payment providers. Allows dynamic selection of payment processing strategies.

**Key Features:**
- Multiple payment strategies (Stripe, PayPal)
- Strategy factory for selection
- Currency support checking
- Fee calculation per provider
- Refund processing

**Usage Example:**
```csharp
// Get strategy for provider
var strategy = _paymentFactory.GetStrategy("stripe");

// Get strategy for currency
var strategy = _paymentFactory.GetStrategyForCurrency("USD");

// Process payment
var result = await strategy.ProcessPaymentAsync(new PaymentRequest
{
    AccountId = accountId,
    Amount = 100.00m,
    Currency = "USD",
    ReferenceNumber = "REF-123"
});

// Process refund
var refund = await strategy.ProcessRefundAsync(new RefundRequest
{
    TransactionId = result.TransactionId,
    Amount = 100.00m,
    Reason = "Customer request"
});
```

##### Factory Pattern (Transaction Creation)
**Location:** `Services/Patterns/TransactionFactory.cs`

**Description:** Factory pattern for transaction creation. Creates different types of transactions based on transaction type.

**Key Features:**
- Transaction factory
- Multiple transaction types (Deposit, Withdrawal, Transfer)
- Transaction validation
- Transaction processing
- Transaction reversal

**Usage Example:**
```csharp
// Create transaction
var context = new TransactionContext
{
    AccountId = accountId,
    Amount = 100.00m,
    Currency = "USD",
    ReferenceNumber = "REF-123"
};

var transaction = _transactionFactory.CreateTransaction(TransactionType.Deposit, context);

// Validate transaction
var isValid = await transaction.ValidateAsync();

// Process transaction
var success = await transaction.ProcessAsync();

// Reverse transaction
var reversed = await transaction.ReverseAsync();
```

## Configuration

All services are configured in `appsettings.json`:

```json
{
  "Sharding": {
    "Shards": [
      {
        "Id": "shard_savings",
        "ConnectionString": "...",
        "ShardType": 0,
        "Weight": 1
      }
    ]
  },
  "ReadReplica": {
    "PrimaryConnectionString": "...",
    "Replicas": [...]
  },
  "Cache": {
    "DefaultExpiration": "00:30:00",
    "KeyPrefix": "bankcore:"
  },
  "ConnectionPool": {
    "MaxPoolSize": 100,
    "ConnectionLifetime": "00:30:00"
  },
  "RateLimit": {
    "BucketCapacity": 100,
    "RefillRate": 1.0
  },
  "Idempotency": {
    "Expiration": "1.00:00:00"
  },
  "ErrorHandling": {
    "RetryCount": 3,
    "ExceptionsAllowedBeforeBreaking": 5
  },
  "Capacity": {
    "CoresPerServer": 4,
    "MemoryPerServerGB": 16
  },
  "BankingCompliance": {
    "ApplicableRegulations": ["PCI DSS", "GDPR", "SOX", "GLBA"],
    "DataRetentionYears": 7
  }
}
```

## Service Registration

All services are registered in `Program.cs`:

```csharp
// HLD/LLD Pattern Services
builder.Services.AddSingleton<SagaManager>();
builder.Services.AddSingleton<DatabaseShardingService>();
builder.Services.AddSingleton<ReadReplicaService>();
builder.Services.AddSingleton<DistributedCacheService>();
builder.Services.AddScoped<IUnitOfWork, UnitOfWork>();
builder.Services.AddScoped(typeof(IRepository<>), typeof(Repository<>));
builder.Services.AddScoped(typeof(IPaginatedRepository<>), typeof(PaginatedRepository<>));
builder.Services.AddSingleton<ConnectionPoolService>();
builder.Services.AddScoped<DatabaseIndexingService>();
builder.Services.AddSingleton<RateLimitingService>();
builder.Services.AddSingleton<IdempotencyService>();
builder.Services.AddSingleton<StructuredLoggingService>();
builder.Services.AddSingleton<ErrorHandlingService>();
builder.Services.AddScoped<IPaymentStrategy, StripePaymentStrategy>();
builder.Services.AddScoped<IPaymentStrategy, PayPalPaymentStrategy>();
builder.Services.AddSingleton<PaymentStrategyFactory>();
builder.Services.AddSingleton<ITransactionFactory, TransactionFactory>();
builder.Services.AddSingleton<CapacityEstimationService>();
builder.Services.AddSingleton<SystemConstraintsAnalysisService>();
```

## Architecture Diagram

```
┌─────────────────────────────────────────────────────────────┐
│                     API Gateway Layer                        │
│  (JWT Auth, Rate Limiting, Request Routing)                │
└─────────────────────────────────────────────────────────────┘
                              │
┌─────────────────────────────────────────────────────────────┐
│                   Application Layer                          │
│  ┌──────────────┐  ┌──────────────┐  ┌──────────────┐      │
│  │ Controllers  │  │   Services   │  │   Patterns   │      │
│  └──────────────┘  └──────────────┘  └──────────────┘      │
└─────────────────────────────────────────────────────────────┘
                              │
┌─────────────────────────────────────────────────────────────┐
│                   Business Logic Layer                      │
│  ┌──────────────┐  ┌──────────────┐  ┌──────────────┐      │
│  │ Saga Manager │  │   Repository │  │ Unit of Work │      │
│  └──────────────┘  └──────────────┘  └──────────────┘      │
└─────────────────────────────────────────────────────────────┘
                              │
┌─────────────────────────────────────────────────────────────┐
│                   Infrastructure Layer                        │
│  ┌──────────────┐  ┌──────────────┐  ┌──────────────┐      │
│  │   Sharding   │  │ Read Replicas│  │ Distributed  │      │
│  │   Service    │  │   Service    │  │    Cache     │      │
│  └──────────────┘  └──────────────┘  └──────────────┘      │
│  ┌──────────────┐  ┌──────────────┐  ┌──────────────┐      │
│  │Connection    │  │   Rate       │  │ Idempotency  │      │
│  │   Pool       │  │   Limiting   │  │   Service    │      │
│  └──────────────┘  └──────────────┘  └──────────────┘      │
└─────────────────────────────────────────────────────────────┘
                              │
┌─────────────────────────────────────────────────────────────┐
│                   Data Layer                                 │
│  ┌──────────────┐  ┌──────────────┐  ┌──────────────┐      │
│  │ PostgreSQL   │  │    Redis     │  │   MongoDB    │      │
│  │  (Shards)    │  │   (Cache)    │  │   (Logs)     │      │
│  └──────────────┘  └──────────────┘  └──────────────┘      │
└─────────────────────────────────────────────────────────────┘
```

## Monitoring and Observability

### Health Checks
- Database connection health
- Redis connection health
- Shard health status
- Replica health status
- Connection pool statistics

### Metrics
- Request rate
- Response times (P50, P95, P99)
- Error rates
- Cache hit/miss ratios
- Connection pool utilization
- Rate limiting statistics

### Logging
- Structured logging with correlation IDs
- Audit event logging
- Transaction event logging
- Error logging with stack traces

## Security Features

- JWT authentication
- API rate limiting
- Idempotency protection
- Structured audit logging
- Encryption at rest (configurable)
- Encryption in transit (TLS)

## Compliance Features

- PCI DSS compliance tracking
- GDPR compliance tracking
- Data retention policies
- Audit log retention
- Compliance scoring
- Regulatory reporting

## Performance Optimizations

- Database sharding for horizontal scaling
- Read replicas for read-heavy workloads
- Distributed caching for reduced latency
- Connection pooling for reduced overhead
- Database indexing for query optimization
- Cursor-based pagination for large datasets

## Resilience Features

- Retry with exponential backoff
- Circuit breaker pattern
- Timeout handling
- Fallback strategies
- Automatic failover
- Health monitoring

## Testing Considerations

### Unit Tests
- Repository pattern tests
- Unit of Work tests
- Service layer tests
- Pattern implementation tests

### Integration Tests
- Database sharding tests
- Read replica tests
- Distributed cache tests
- Rate limiting tests
- Idempotency tests

### Performance Tests
- Load testing with capacity estimation
- Stress testing with rate limiting
- Failover testing with circuit breaker
- Latency testing with caching

## Deployment Considerations

### Environment Variables
- All sensitive configuration via environment variables
- Separate configurations for dev/staging/prod
- Feature flags for experimental features

### Scaling
- Horizontal scaling with sharding
- Vertical scaling with capacity estimation
- Auto-scaling based on metrics
- Multi-region deployment with read replicas

### Monitoring
- Application Performance Monitoring (APM)
- Log aggregation
- Metrics collection and alerting
- Distributed tracing

## Best Practices

1. **Always use Unit of Work** for transactional operations
2. **Use Repository Pattern** for data access abstraction
3. **Implement idempotency** for all state-changing operations
4. **Use structured logging** with correlation IDs
5. **Apply rate limiting** to all public endpoints
6. **Use caching** for frequently accessed data
7. **Implement circuit breakers** for external service calls
8. **Monitor shard health** and rebalance as needed
9. **Track replica lag** and ensure data consistency
10. **Regular capacity planning** based on growth projections

## Troubleshooting

### Common Issues

**Connection Pool Exhaustion**
- Check pool statistics
- Increase max pool size
- Optimize connection usage

**Cache Stampede**
- Implement cache locking
- Use cache warming
- Monitor cache hit rates

**Circuit Breaker Tripping**
- Check external service health
- Adjust circuit breaker thresholds
- Implement fallback strategies

**Shard Imbalance**
- Monitor shard distribution
- Rebalance shards
- Adjust shard weights

## References

- [Saga Pattern](https://microservices.io/patterns/data/saga.html)
- [Repository Pattern](https://martinfowler.com/eaaCatalog/repository.html)
- [Unit of Work Pattern](https://martinfowler.com/eaaCatalog/unitOfWork.html)
- [Token Bucket Algorithm](https://en.wikipedia.org/wiki/Token_bucket)
- [Circuit Breaker Pattern](https://martinfowler.com/bliki/CircuitBreaker.html)
- [Strategy Pattern](https://refactoring.guru/design-patterns/strategy)
- [Factory Pattern](https://refactoring.guru/design-patterns/factory-method)

## Summary

This implementation provides a comprehensive set of HLD and LLD patterns for a production-ready banking application. All patterns are implemented with best practices, proper error handling, monitoring, and documentation. The system is designed for scalability, reliability, and compliance with banking regulations.
