using System.Net;
using Microsoft.Extensions.Options;
using Polly;
using Polly.CircuitBreaker;
using Polly.Fallback;
using Polly.Retry;

namespace BankCore.Services.ErrorHandling
{
    /// <summary>
    /// Resilience helper (retry with exponential backoff, circuit breaker, timeout, fallback)
    /// for calls to external services such as payment gateways. Built on Polly 8 resilience pipelines.
    /// </summary>
    public class ErrorHandlingService
    {
        private readonly ILogger<ErrorHandlingService> _logger;
        private readonly ErrorHandlingOptions _options;

        private readonly CircuitBreakerStateProvider _circuitState = new();
        private readonly CircuitBreakerManualControl _circuitControl = new();
        private readonly ResiliencePipeline _retry;
        private readonly ResiliencePipeline _breaker;
        private readonly ResiliencePipeline _timeout;
        private readonly ResiliencePipeline<HttpResponseMessage> _httpFallback;

        public ErrorHandlingService(
            IOptions<ErrorHandlingOptions> options,
            ILogger<ErrorHandlingService> logger)
        {
            _options = options.Value;
            _logger = logger;

            _retry = new ResiliencePipelineBuilder()
                .AddRetry(new RetryStrategyOptions
                {
                    MaxRetryAttempts = _options.RetryCount,
                    BackoffType = DelayBackoffType.Exponential,
                    Delay = TimeSpan.FromSeconds(1),
                    UseJitter = true,
                    OnRetry = args =>
                    {
                        _logger.LogWarning(
                            "Retry {Attempt} after {Delay}s due to: {Message}",
                            args.AttemptNumber + 1,
                            args.RetryDelay.TotalSeconds,
                            args.Outcome.Exception?.Message);
                        return default;
                    }
                })
                .Build();

            // Opens after N failures within the sampling window (every call in the window must fail).
            _breaker = new ResiliencePipelineBuilder()
                .AddCircuitBreaker(new CircuitBreakerStrategyOptions
                {
                    FailureRatio = 1.0,
                    MinimumThroughput = Math.Max(2, _options.ExceptionsAllowedBeforeBreaking),
                    SamplingDuration = TimeSpan.FromSeconds(30),
                    BreakDuration = _options.BreakDuration,
                    StateProvider = _circuitState,
                    ManualControl = _circuitControl,
                    OnOpened = args =>
                    {
                        _logger.LogError("Circuit breaker opened for {Seconds}s", args.BreakDuration.TotalSeconds);
                        return default;
                    },
                    OnClosed = _ =>
                    {
                        _logger.LogInformation("Circuit breaker closed");
                        return default;
                    }
                })
                .Build();

            _timeout = new ResiliencePipelineBuilder()
                .AddTimeout(_options.TimeoutDuration)
                .Build();

            _httpFallback = new ResiliencePipelineBuilder<HttpResponseMessage>()
                .AddFallback(new FallbackStrategyOptions<HttpResponseMessage>
                {
                    ShouldHandle = new PredicateBuilder<HttpResponseMessage>().Handle<Exception>(),
                    FallbackAction = args =>
                    {
                        _logger.LogError("Fallback executed due to: {Message}", args.Outcome.Exception?.Message);
                        return Outcome.FromResultAsValueTask(
                            new HttpResponseMessage(HttpStatusCode.ServiceUnavailable));
                    }
                })
                .Build();
        }

        /// <summary>Execute an operation with retry and exponential backoff.</summary>
        public async Task<T> ExecuteWithRetryAsync<T>(Func<Task<T>> operation)
        {
            return await _retry.ExecuteAsync(async _ => await operation(), CancellationToken.None);
        }

        /// <summary>Execute an operation behind the circuit breaker.</summary>
        public async Task<T> ExecuteWithCircuitBreakerAsync<T>(Func<Task<T>> operation)
        {
            return await _breaker.ExecuteAsync(async _ => await operation(), CancellationToken.None);
        }

        /// <summary>Execute an operation with a timeout.</summary>
        public async Task<T> ExecuteWithTimeoutAsync<T>(Func<Task<T>> operation)
        {
            return await _timeout.ExecuteAsync(async _ => await operation(), CancellationToken.None);
        }

        /// <summary>Execute an operation with timeout, circuit breaker and retry (outermost to innermost).</summary>
        public async Task<T> ExecuteWithResilienceAsync<T>(Func<Task<T>> operation)
        {
            return await _timeout.ExecuteAsync(
                async ct => await _breaker.ExecuteAsync(
                    async ct2 => await _retry.ExecuteAsync(async _ => await operation(), ct2),
                    ct),
                CancellationToken.None);
        }

        /// <summary>Execute an HTTP call with all policies and a 503 fallback response.</summary>
        public async Task<HttpResponseMessage> ExecuteHttpRequestWithResilienceAsync(
            Func<Task<HttpResponseMessage>> operation)
        {
            return await _httpFallback.ExecuteAsync(
                async ct => await ExecuteWithResilienceAsync(operation),
                CancellationToken.None);
        }

        public CircuitState GetCircuitBreakerState() => _circuitState.CircuitState;

        public async Task ResetCircuitBreakerAsync()
        {
            await _circuitControl.CloseAsync(CancellationToken.None);
            _logger.LogInformation("Circuit breaker manually reset");
        }

        public async Task IsolateCircuitBreakerAsync()
        {
            await _circuitControl.IsolateAsync(CancellationToken.None);
            _logger.LogWarning("Circuit breaker manually isolated");
        }

        public ErrorHandlingStatistics GetStatistics()
        {
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

    public class ErrorHandlingOptions
    {
        public int RetryCount { get; set; } = 3;
        public int ExceptionsAllowedBeforeBreaking { get; set; } = 5;
        public TimeSpan BreakDuration { get; set; } = TimeSpan.FromMinutes(1);
        public TimeSpan TimeoutDuration { get; set; } = TimeSpan.FromSeconds(30);
    }

    public class ErrorHandlingStatistics
    {
        public string CircuitState { get; set; } = string.Empty;
        public int RetryCount { get; set; }
        public int ExceptionsAllowedBeforeBreaking { get; set; }
        public TimeSpan TimeoutDuration { get; set; }
        public DateTime LastUpdated { get; set; }
    }
}
