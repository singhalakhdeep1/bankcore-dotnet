using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Polly;
using Polly.CircuitBreaker;
using Polly.Retry;
using Polly.Timeout;

namespace BankCore.Services.ErrorHandling
{
    /// <summary>
    /// Error Handling Service with Retry and Circuit Breaker Patterns
    /// Implements resilience patterns for external service calls
    /// </summary>
    public class ErrorHandlingService
    {
        private readonly Dictionary<string, IAsyncPolicy> _policies = new();
        private readonly ILogger<ErrorHandlingService> _logger;
        private readonly ErrorHandlingOptions _options;

        public ErrorHandlingService(
            IOptions<ErrorHandlingOptions> options,
            ILogger<ErrorHandlingService> logger)
        {
            _options = options.Value;
            _logger = logger;
            
            InitializePolicies();
        }

        /// <summary>
        /// Initialize resilience policies
        /// </summary>
        private void InitializePolicies()
        {
            // Retry policy with exponential backoff
            var retryPolicy = Policy
                .Handle<Exception>()
                .WaitAndRetryAsync(
                    _options.RetryCount,
                    retryAttempt => TimeSpan.FromSeconds(Math.Pow(2, retryAttempt)),
                    onRetry: (outcome, timespan, retryCount, context) =>
                    {
                        _logger.LogWarning(
                            $"Retry {retryCount} after {timespan.TotalSeconds}s due to: {outcome.Exception?.Message}");
                    });

            // Circuit breaker policy
            var circuitBreakerPolicy = Policy
                .Handle<Exception>()
                .CircuitBreakerAsync(
                    exceptionsAllowedBeforeBreaking: _options.ExceptionsAllowedBeforeBreaking,
                    durationOfBreak: _options.BreakDuration,
                    onBreak: (exception, breakDelay) =>
                    {
                        _logger.LogError($"Circuit breaker opened for {breakDelay.TotalSeconds}s due to: {exception.Message}");
                    },
                    onReset: () =>
                    {
                        _logger.LogInformation("Circuit breaker reset");
                    });

            // Timeout policy
            var timeoutPolicy = Policy
                .TimeoutAsync(
                    _options.TimeoutDuration,
                    TimeoutStrategy.Optimistic,
                    onTimeout: (context, timespan, task) =>
                    {
                        _logger.LogWarning($"Operation timed out after {timespan.TotalSeconds}s");
                    });

            // Fallback policy
            var fallbackPolicy = Policy<HttpResponseMessage>
                .Handle<Exception>()
                .FallbackAsync(
                    fallbackValue: new HttpResponseMessage(System.Net.HttpStatusCode.ServiceUnavailable),
                    onFallback: (exception) =>
                    {
                        _logger.LogError($"Fallback executed due to: {exception.Exception?.Message}");
                        return Task.CompletedTask;
                    });

            _policies["Retry"] = retryPolicy;
            _policies["CircuitBreaker"] = circuitBreakerPolicy;
            _policies["Timeout"] = timeoutPolicy;
            _policies["Fallback"] = fallbackPolicy;
        }

        /// <summary>
        /// Execute operation with retry policy
        /// </summary>
        public async Task<T> ExecuteWithRetryAsync<T>(Func<Task<T>> operation)
        {
            var policy = _policies["Retry"] as AsyncRetryPolicy;
            return await policy.ExecuteAsync(operation);
        }

        /// <summary>
        /// Execute operation with circuit breaker
        /// </summary>
        public async Task<T> ExecuteWithCircuitBreakerAsync<T>(Func<Task<T>> operation)
        {
            var policy = _policies["CircuitBreaker"] as AsyncCircuitBreakerPolicy;
            return await policy.ExecuteAsync(operation);
        }

        /// <summary>
        /// Execute operation with timeout
        /// </summary>
        public async Task<T> ExecuteWithTimeoutAsync<T>(Func<Task<T>> operation)
        {
            var policy = _policies["Timeout"] as AsyncTimeoutPolicy;
            return await policy.ExecuteAsync(operation);
        }

        /// <summary>
        /// Execute operation with all resilience policies
        /// </summary>
        public async Task<T> ExecuteWithResilienceAsync<T>(Func<Task<T>> operation)
        {
            var resilienceStrategy = Policy.WrapAsync(
                _policies["Timeout"],
                _policies["CircuitBreaker"],
                _policies["Retry"]);

            return await resilienceStrategy.ExecuteAsync(operation);
        }

        /// <summary>
        /// Execute HTTP request with resilience
        /// </summary>
        public async Task<HttpResponseMessage> ExecuteHttpRequestWithResilienceAsync(
            Func<Task<HttpResponseMessage>> operation)
        {
            var resilienceStrategy = Policy.WrapAsync(
                _policies["Timeout"],
                _policies["CircuitBreaker"],
                _policies["Retry"],
                _policies["Fallback"]);

            return await resilienceStrategy.ExecuteAsync(operation);
        }

        /// <summary>
        /// Get circuit breaker state
        /// </summary>
        public CircuitState GetCircuitBreakerState()
        {
            var policy = _policies["CircuitBreaker"] as AsyncCircuitBreakerPolicy;
            
            return policy.CircuitState switch
            {
                CircuitState.Closed => CircuitState.Closed,
                CircuitState.Open => CircuitState.Open,
                CircuitState.HalfOpen => CircuitState.HalfOpen,
                CircuitState.Isolated => CircuitState.Isolated,
                _ => CircuitState.Closed
            };
        }

        /// <summary>
        /// Reset circuit breaker manually
        /// </summary>
        public void ResetCircuitBreaker()
        {
            var policy = _policies["CircuitBreaker"] as AsyncCircuitBreakerPolicy;
            policy.Reset();
            _logger.LogInformation("Circuit breaker manually reset");
        }

        /// <summary>
        /// Isolate circuit breaker (manual open)
        /// </summary>
        public void IsolateCircuitBreaker()
        {
            var policy = _policies["CircuitBreaker"] as AsyncCircuitBreakerPolicy;
            policy.Isolate();
            _logger.LogWarning("Circuit breaker manually isolated");
        }

        /// <summary>
        /// Get error handling statistics
        /// </summary>
        public ErrorHandlingStatistics GetStatistics()
        {
            var policy = _policies["CircuitBreaker"] as AsyncCircuitBreakerPolicy;
            
            return new ErrorHandlingStatistics
            {
                CircuitState = GetCircuitBreakerState().ToString(),
                RetryCount = _options.RetryCount,
                ExceptionsAllowedBeforeBreaking = _options.ExceptionsAllowedBeforeBreaking,
                TimeoutDuration = _options.TimeoutDuration,
                LastUpdated = DateTime.UtcNow
            };
        }
    }

    /// <summary>
    /// Error handling options
    /// </summary>
    public class ErrorHandlingOptions
    {
        public int RetryCount { get; set; } = 3;
        public int ExceptionsAllowedBeforeBreaking { get; set; } = 5;
        public TimeSpan BreakDuration { get; set; } = TimeSpan.FromMinutes(1);
        public TimeSpan TimeoutDuration { get; set; } = TimeSpan.FromSeconds(30);
    }

    /// <summary>
    /// Error handling statistics
    /// </summary>
    public class ErrorHandlingStatistics
    {
        public string CircuitState { get; set; }
        public int RetryCount { get; set; }
        public int ExceptionsAllowedBeforeBreaking { get; set; }
        public TimeSpan TimeoutDuration { get; set; }
        public DateTime LastUpdated { get; set; }
    }

    /// <summary>
    /// HTTP Response message for fallback
    /// </summary>
    public class HttpResponseMessage
    {
        public HttpStatusCode StatusCode { get; set; }

        public HttpResponseMessage(HttpStatusCode statusCode)
        {
            StatusCode = statusCode;
        }
    }

    /// <summary>
    /// HTTP Status codes
    /// </summary>
    public enum HttpStatusCode
    {
        OK = 200,
        ServiceUnavailable = 503
    }
}
