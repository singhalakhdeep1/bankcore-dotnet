using Microsoft.Extensions.Options;
using System;
using System.Threading.Tasks;

namespace BankCore.Services.Patterns
{
    /// <summary>
    /// Strategy Pattern for Payment Providers
    /// Allows dynamic selection of payment processing strategies
    /// </summary>
    public interface IPaymentStrategy
    {
        string ProviderName { get; }
        Task<PaymentResult> ProcessPaymentAsync(PaymentRequest request);
        Task<RefundResult> ProcessRefundAsync(RefundRequest request);
        bool SupportsCurrency(string currency);
        decimal CalculateFee(decimal amount);
    }

    /// <summary>
    /// Payment request
    /// </summary>
    public class PaymentRequest
    {
        public string AccountId { get; set; } = string.Empty;
        public decimal Amount { get; set; }
        public string Currency { get; set; } = string.Empty;
        public string ReferenceNumber { get; set; } = string.Empty;
        public string Description { get; set; } = string.Empty;
        public Dictionary<string, string> Metadata { get; set; } = new();
    }

    /// <summary>
    /// Payment result
    /// </summary>
    public class PaymentResult
    {
        public bool Success { get; set; }
        public string TransactionId { get; set; } = string.Empty;
        public string Message { get; set; } = string.Empty;
        public decimal Fee { get; set; }
        public DateTime ProcessedAt { get; set; }
        public Dictionary<string, string> Metadata { get; set; } = new();
    }

    /// <summary>
    /// Refund request
    /// </summary>
    public class RefundRequest
    {
        public string TransactionId { get; set; } = string.Empty;
        public decimal Amount { get; set; }
        public string Reason { get; set; } = string.Empty;
    }

    /// <summary>
    /// Refund result
    /// </summary>
    public class RefundResult
    {
        public bool Success { get; set; }
        public string RefundId { get; set; } = string.Empty;
        public string Message { get; set; } = string.Empty;
        public DateTime ProcessedAt { get; set; }
    }

    /// <summary>
    /// Stripe payment strategy
    /// </summary>
    public class StripePaymentStrategy : IPaymentStrategy
    {
        private readonly ILogger<StripePaymentStrategy> _logger;
        private readonly StripeOptions _options;

        public string ProviderName => "Stripe";

        public StripePaymentStrategy(
            IOptions<StripeOptions> options,
            ILogger<StripePaymentStrategy> logger)
        {
            _options = options.Value;
            _logger = logger;
        }

        public async Task<PaymentResult> ProcessPaymentAsync(PaymentRequest request)
        {
            _logger.LogInformation($"Processing Stripe payment: {request.ReferenceNumber}");

            // Simulate Stripe API call
            await Task.Delay(100);

            return new PaymentResult
            {
                Success = true,
                TransactionId = $"stripe_{Guid.NewGuid()}",
                Message = "Payment processed successfully",
                Fee = CalculateFee(request.Amount),
                ProcessedAt = DateTime.UtcNow,
                Metadata = new Dictionary<string, string>
                {
                    ["provider"] = ProviderName,
                    ["reference"] = request.ReferenceNumber
                }
            };
        }

        public async Task<RefundResult> ProcessRefundAsync(RefundRequest request)
        {
            _logger.LogInformation($"Processing Stripe refund: {request.TransactionId}");

            await Task.Delay(100);

            return new RefundResult
            {
                Success = true,
                RefundId = $"refund_{Guid.NewGuid()}",
                Message = "Refund processed successfully",
                ProcessedAt = DateTime.UtcNow
            };
        }

        public bool SupportsCurrency(string currency)
        {
            return currency.ToUpper() switch
            {
                "USD" or "EUR" or "GBP" or "CAD" or "AUD" => true,
                _ => false
            };
        }

        public decimal CalculateFee(decimal amount)
        {
            // Stripe fee: 2.9% + $0.30
            return (amount * 0.029m) + 0.30m;
        }
    }

    /// <summary>
    /// PayPal payment strategy
    /// </summary>
    public class PayPalPaymentStrategy : IPaymentStrategy
    {
        private readonly ILogger<PayPalPaymentStrategy> _logger;
        private readonly PayPalOptions _options;

        public string ProviderName => "PayPal";

        public PayPalPaymentStrategy(
            IOptions<PayPalOptions> options,
            ILogger<PayPalPaymentStrategy> logger)
        {
            _options = options.Value;
            _logger = logger;
        }

        public async Task<PaymentResult> ProcessPaymentAsync(PaymentRequest request)
        {
            _logger.LogInformation($"Processing PayPal payment: {request.ReferenceNumber}");

            await Task.Delay(100);

            return new PaymentResult
            {
                Success = true,
                TransactionId = $"paypal_{Guid.NewGuid()}",
                Message = "Payment processed successfully",
                Fee = CalculateFee(request.Amount),
                ProcessedAt = DateTime.UtcNow,
                Metadata = new Dictionary<string, string>
                {
                    ["provider"] = ProviderName,
                    ["reference"] = request.ReferenceNumber
                }
            };
        }

        public async Task<RefundResult> ProcessRefundAsync(RefundRequest request)
        {
            _logger.LogInformation($"Processing PayPal refund: {request.TransactionId}");

            await Task.Delay(100);

            return new RefundResult
            {
                Success = true,
                RefundId = $"refund_{Guid.NewGuid()}",
                Message = "Refund processed successfully",
                ProcessedAt = DateTime.UtcNow
            };
        }

        public bool SupportsCurrency(string currency)
        {
            return currency.ToUpper() switch
            {
                "USD" or "EUR" or "GBP" or "CAD" or "AUD" or "JPY" => true,
                _ => false
            };
        }

        public decimal CalculateFee(decimal amount)
        {
            // PayPal fee: 2.9% + $0.30
            return (amount * 0.029m) + 0.30m;
        }
    }

    /// <summary>
    /// Payment strategy factory
    /// </summary>
    public class PaymentStrategyFactory
    {
        private readonly IServiceProvider _serviceProvider;
        private readonly ILogger<PaymentStrategyFactory> _logger;

        public PaymentStrategyFactory(
            IServiceProvider serviceProvider,
            ILogger<PaymentStrategyFactory> logger)
        {
            _serviceProvider = serviceProvider;
            _logger = logger;
        }

        public IPaymentStrategy GetStrategy(string providerName)
        {
            return providerName.ToLower() switch
            {
                "stripe" => _serviceProvider.GetRequiredService<StripePaymentStrategy>(),
                "paypal" => _serviceProvider.GetRequiredService<PayPalPaymentStrategy>(),
                _ => throw new ArgumentException($"Unknown payment provider: {providerName}")
            };
        }

        public IPaymentStrategy GetStrategyForCurrency(string currency)
        {
            // Get all strategies and find first that supports the currency
            var strategies = new List<IPaymentStrategy>
            {
                _serviceProvider.GetRequiredService<StripePaymentStrategy>(),
                _serviceProvider.GetRequiredService<PayPalPaymentStrategy>()
            };

            var strategy = strategies.FirstOrDefault(s => s.SupportsCurrency(currency));
            
            if (strategy == null)
            {
                throw new ArgumentException($"No payment provider supports currency: {currency}");
            }

            return strategy;
        }
    }

    /// <summary>
    /// Stripe options
    /// </summary>
    public class StripeOptions
    {
        public string ApiKey { get; set; } = string.Empty;
        public string WebhookSecret { get; set; } = string.Empty;
    }

    /// <summary>
    /// PayPal options
    /// </summary>
    public class PayPalOptions
    {
        public string ClientId { get; set; } = string.Empty;
        public string ClientSecret { get; set; } = string.Empty;
        public string Mode { get; set; } = "sandbox";
    }
}
