using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Options;
using Stripe;

namespace BankCore.Services.Patterns
{
    /// <summary>
    /// Strategy pattern for payment providers: the payment saga talks to <see cref="IPaymentStrategy"/>
    /// and <see cref="PaymentStrategyFactory"/> picks the provider.
    /// Each strategy talks to the real provider when credentials are configured and otherwise runs in
    /// a deterministic simulated mode (reported as <c>mode=simulated</c> in result metadata).
    /// </summary>
    public interface IPaymentStrategy
    {
        string ProviderName { get; }

        /// <summary>True when real provider credentials are configured; false means simulated mode.</summary>
        bool IsLive { get; }

        Task<TokenizeResult> TokenizeAsync(TokenizeRequest request);
        Task<PaymentResult> ProcessPaymentAsync(PaymentRequest request);
        Task<RefundResult> ProcessRefundAsync(RefundRequest request);
        bool SupportsCurrency(string currency);
        decimal CalculateFee(decimal amount);
    }

    public class TokenizeRequest
    {
        /// <summary>Provider-issued token from the client SDK (Stripe.js pm_..., PayPal vault id). Preferred for live mode.</summary>
        public string? ProviderToken { get; set; }

        /// <summary>Raw card details are only accepted in simulated mode and are never stored or logged.</summary>
        public string? CardNumber { get; set; }
        public int ExpMonth { get; set; }
        public int ExpYear { get; set; }
        public string? Cvc { get; set; }
    }

    public class TokenizeResult
    {
        public bool Success { get; set; }
        public string Token { get; set; } = string.Empty;
        public string Brand { get; set; } = string.Empty;
        public string Last4 { get; set; } = string.Empty;
        public int ExpMonth { get; set; }
        public int ExpYear { get; set; }
        public string Message { get; set; } = string.Empty;
    }

    public class PaymentRequest
    {
        public string AccountId { get; set; } = string.Empty;
        public decimal Amount { get; set; }
        public string Currency { get; set; } = string.Empty;
        public string ReferenceNumber { get; set; } = string.Empty;
        public string Description { get; set; } = string.Empty;

        /// <summary>Tokenized payment method (see <see cref="IPaymentStrategy.TokenizeAsync"/>).</summary>
        public string PaymentMethodToken { get; set; } = string.Empty;

        public Dictionary<string, string> Metadata { get; set; } = new();
    }

    public class PaymentResult
    {
        public bool Success { get; set; }
        public string TransactionId { get; set; } = string.Empty;
        public string Message { get; set; } = string.Empty;
        public decimal Fee { get; set; }
        public DateTime ProcessedAt { get; set; }
        public Dictionary<string, string> Metadata { get; set; } = new();
    }

    public class RefundRequest
    {
        public string TransactionId { get; set; } = string.Empty;
        public decimal Amount { get; set; }
        public string Reason { get; set; } = string.Empty;
    }

    public class RefundResult
    {
        public bool Success { get; set; }
        public string RefundId { get; set; } = string.Empty;
        public string Message { get; set; } = string.Empty;
        public DateTime ProcessedAt { get; set; }
    }

    /// <summary>Card helpers: Luhn check, brand detection, and the simulated gateway's behaviour.</summary>
    public static class CardHelper
    {
        public static string Digits(string? input) => new((input ?? string.Empty).Where(char.IsDigit).ToArray());

        public static bool PassesLuhn(string digits)
        {
            if (digits.Length < 12 || digits.Length > 19) return false;

            var sum = 0;
            var alternate = false;
            for (var i = digits.Length - 1; i >= 0; i--)
            {
                var n = digits[i] - '0';
                if (alternate)
                {
                    n *= 2;
                    if (n > 9) n -= 9;
                }
                sum += n;
                alternate = !alternate;
            }

            return sum % 10 == 0;
        }

        public static string Brand(string digits)
        {
            if (digits.StartsWith('4')) return "visa";
            if (digits.Length >= 2)
            {
                var two = int.Parse(digits[..2]);
                if (two is >= 51 and <= 55) return "mastercard";
                if (two is 34 or 37) return "amex";
                if (two == 65) return "discover";
            }
            if (digits.Length >= 4)
            {
                var four = int.Parse(digits[..4]);
                if (four is >= 2221 and <= 2720) return "mastercard";
                if (four == 6011) return "discover";
            }
            return "card";
        }

        /// <summary>
        /// Simulated-mode tokenization. The token encodes how a later charge should behave,
        /// mirroring Stripe's test cards: 4000000000000002 declines, 4000000000009995 has insufficient funds.
        /// </summary>
        public static TokenizeResult SimulatedTokenize(string provider, TokenizeRequest request)
        {
            var digits = Digits(request.CardNumber);
            if (!PassesLuhn(digits))
            {
                return new TokenizeResult { Success = false, Message = "Invalid card number" };
            }

            var now = DateTime.UtcNow;
            var expired = request.ExpYear < now.Year || (request.ExpYear == now.Year && request.ExpMonth < now.Month);
            if (request.ExpMonth is < 1 or > 12 || expired)
            {
                return new TokenizeResult { Success = false, Message = "Card is expired or the expiry date is invalid" };
            }

            var behaviour = digits switch
            {
                "4000000000000002" => "declined",
                "4000000000009995" => "nsf",
                _ => "ok"
            };

            return new TokenizeResult
            {
                Success = true,
                Token = $"sim_{provider.ToLowerInvariant()}_{behaviour}_{Guid.NewGuid():N}",
                Brand = Brand(digits),
                Last4 = digits[^4..],
                ExpMonth = request.ExpMonth,
                ExpYear = request.ExpYear,
                Message = "Tokenized (simulated mode)"
            };
        }

        public static PaymentResult SimulatedCharge(string provider, PaymentRequest request, decimal fee)
        {
            var token = request.PaymentMethodToken ?? string.Empty;
            var success = true;
            var message = "Payment processed successfully (simulated)";

            if (token.Contains("_declined_", StringComparison.Ordinal))
            {
                success = false;
                message = "Your card was declined.";
            }
            else if (token.Contains("_nsf_", StringComparison.Ordinal))
            {
                success = false;
                message = "Your card has insufficient funds.";
            }

            return new PaymentResult
            {
                Success = success,
                TransactionId = success ? $"sim_{provider.ToLowerInvariant()}_{Guid.NewGuid():N}" : string.Empty,
                Message = message,
                Fee = success ? fee : 0,
                ProcessedAt = DateTime.UtcNow,
                Metadata = new Dictionary<string, string>
                {
                    ["provider"] = provider,
                    ["mode"] = "simulated",
                    ["reference"] = request.ReferenceNumber
                }
            };
        }
    }

    /// <summary>Stripe strategy backed by Stripe.net (PaymentIntents) when a secret key is configured.</summary>
    public class StripePaymentStrategy : IPaymentStrategy
    {
        private readonly ILogger<StripePaymentStrategy> _logger;
        private readonly StripeOptions _options;

        public string ProviderName => "Stripe";
        public bool IsLive => _options.IsConfigured;

        public StripePaymentStrategy(IOptions<StripeOptions> options, ILogger<StripePaymentStrategy> logger)
        {
            _options = options.Value;
            _logger = logger;
        }

        public async Task<TokenizeResult> TokenizeAsync(TokenizeRequest request)
        {
            if (!IsLive)
            {
                return CardHelper.SimulatedTokenize(ProviderName, request);
            }

            if (string.IsNullOrWhiteSpace(request.ProviderToken) || !request.ProviderToken.StartsWith("pm_", StringComparison.Ordinal))
            {
                return new TokenizeResult
                {
                    Success = false,
                    Message = "Send a Stripe PaymentMethod id (pm_...) created by Stripe.js. Raw card numbers are not accepted by the API."
                };
            }

            try
            {
                var method = await new PaymentMethodService(new StripeClient(_options.EffectiveSecretKey))
                    .GetAsync(request.ProviderToken);

                return new TokenizeResult
                {
                    Success = true,
                    Token = method.Id,
                    Brand = method.Card?.Brand ?? "card",
                    Last4 = method.Card?.Last4 ?? string.Empty,
                    ExpMonth = (int)(method.Card?.ExpMonth ?? 0),
                    ExpYear = (int)(method.Card?.ExpYear ?? 0),
                    Message = "Verified with Stripe"
                };
            }
            catch (StripeException ex)
            {
                _logger.LogWarning(ex, "Stripe rejected payment method {Token}", request.ProviderToken);
                return new TokenizeResult { Success = false, Message = ex.StripeError?.Message ?? "Stripe could not verify this payment method" };
            }
        }

        public async Task<PaymentResult> ProcessPaymentAsync(PaymentRequest request)
        {
            if (!IsLive)
            {
                return CardHelper.SimulatedCharge(ProviderName, request, CalculateFee(request.Amount));
            }

            try
            {
                var service = new PaymentIntentService(new StripeClient(_options.EffectiveSecretKey));
                var intent = await service.CreateAsync(
                    new PaymentIntentCreateOptions
                    {
                        Amount = (long)Math.Round(request.Amount * 100m, MidpointRounding.AwayFromZero),
                        Currency = request.Currency.ToLowerInvariant(),
                        PaymentMethod = request.PaymentMethodToken,
                        Confirm = true,
                        Description = request.Description,
                        AutomaticPaymentMethods = new PaymentIntentAutomaticPaymentMethodsOptions
                        {
                            Enabled = true,
                            AllowRedirects = "never"
                        },
                        Metadata = new Dictionary<string, string>
                        {
                            ["reference"] = request.ReferenceNumber,
                            ["account"] = request.AccountId
                        }
                    },
                    new RequestOptions { IdempotencyKey = request.ReferenceNumber });

                var succeeded = intent.Status == "succeeded";
                return new PaymentResult
                {
                    Success = succeeded,
                    TransactionId = intent.Id,
                    Message = succeeded ? "Payment processed successfully" : $"Payment not completed (status: {intent.Status})",
                    Fee = CalculateFee(request.Amount),
                    ProcessedAt = DateTime.UtcNow,
                    Metadata = new Dictionary<string, string>
                    {
                        ["provider"] = ProviderName,
                        ["mode"] = "live",
                        ["reference"] = request.ReferenceNumber,
                        ["status"] = intent.Status
                    }
                };
            }
            catch (StripeException ex) when (ex.StripeError?.Type == "card_error")
            {
                // A declined card is a normal business outcome, not an infrastructure failure.
                return new PaymentResult
                {
                    Success = false,
                    Message = ex.StripeError.Message ?? "Your card was declined.",
                    ProcessedAt = DateTime.UtcNow,
                    Metadata = new Dictionary<string, string> { ["provider"] = ProviderName, ["mode"] = "live", ["code"] = ex.StripeError.Code ?? string.Empty }
                };
            }
        }

        public async Task<RefundResult> ProcessRefundAsync(RefundRequest request)
        {
            if (!IsLive)
            {
                return new RefundResult
                {
                    Success = true,
                    RefundId = $"sim_refund_{Guid.NewGuid():N}",
                    Message = "Refund processed (simulated)",
                    ProcessedAt = DateTime.UtcNow
                };
            }

            var refund = await new RefundService(new StripeClient(_options.EffectiveSecretKey)).CreateAsync(
                new RefundCreateOptions
                {
                    PaymentIntent = request.TransactionId,
                    Amount = (long)Math.Round(request.Amount * 100m, MidpointRounding.AwayFromZero),
                    Reason = "requested_by_customer"
                });

            return new RefundResult
            {
                Success = refund.Status is "succeeded" or "pending",
                RefundId = refund.Id,
                Message = $"Refund {refund.Status}",
                ProcessedAt = DateTime.UtcNow
            };
        }

        public bool SupportsCurrency(string currency) => currency.ToUpperInvariant() is "USD" or "EUR" or "GBP" or "CAD" or "AUD";

        /// <summary>Stripe's standard card fee: 2.9% + 0.30.</summary>
        public decimal CalculateFee(decimal amount) => Math.Round(amount * 0.029m + 0.30m, 2);
    }

    /// <summary>PayPal strategy backed by the PayPal REST API v2 (Orders + Captures) when credentials are configured.</summary>
    public class PayPalPaymentStrategy : IPaymentStrategy
    {
        private readonly ILogger<PayPalPaymentStrategy> _logger;
        private readonly PayPalOptions _options;
        private readonly IHttpClientFactory _httpClientFactory;

        public string ProviderName => "PayPal";
        public bool IsLive => _options.IsConfigured;

        public PayPalPaymentStrategy(
            IOptions<PayPalOptions> options,
            ILogger<PayPalPaymentStrategy> logger,
            IHttpClientFactory httpClientFactory)
        {
            _options = options.Value;
            _logger = logger;
            _httpClientFactory = httpClientFactory;
        }

        public Task<TokenizeResult> TokenizeAsync(TokenizeRequest request)
        {
            if (!IsLive)
            {
                return Task.FromResult(CardHelper.SimulatedTokenize(ProviderName, request));
            }

            // Live PayPal uses a vault/payment-method token created client-side; the server only records it.
            if (string.IsNullOrWhiteSpace(request.ProviderToken))
            {
                return Task.FromResult(new TokenizeResult { Success = false, Message = "Send the PayPal vault token created by the PayPal JS SDK." });
            }

            return Task.FromResult(new TokenizeResult
            {
                Success = true,
                Token = request.ProviderToken,
                Brand = "paypal",
                Last4 = request.ProviderToken.Length >= 4 ? request.ProviderToken[^4..] : request.ProviderToken,
                Message = "Token recorded"
            });
        }

        public async Task<PaymentResult> ProcessPaymentAsync(PaymentRequest request)
        {
            if (!IsLive)
            {
                return CardHelper.SimulatedCharge(ProviderName, request, CalculateFee(request.Amount));
            }

            var client = await CreateAuthorizedClientAsync();
            var body = new
            {
                intent = "CAPTURE",
                purchase_units = new[]
                {
                    new
                    {
                        reference_id = request.ReferenceNumber,
                        description = request.Description,
                        amount = new { currency_code = request.Currency.ToUpperInvariant(), value = request.Amount.ToString("0.00", System.Globalization.CultureInfo.InvariantCulture) }
                    }
                },
                payment_source = new { token = new { id = request.PaymentMethodToken, type = "PAYMENT_METHOD_TOKEN" } }
            };

            using var message = new HttpRequestMessage(HttpMethod.Post, "/v2/checkout/orders")
            {
                Content = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json")
            };
            message.Headers.Add("PayPal-Request-Id", request.ReferenceNumber);

            using var response = await client.SendAsync(message);
            var json = await response.Content.ReadAsStringAsync();
            using var doc = JsonDocument.Parse(json);

            var status = doc.RootElement.TryGetProperty("status", out var s) ? s.GetString() : null;
            var succeeded = response.IsSuccessStatusCode && status == "COMPLETED";

            string captureId = string.Empty;
            if (succeeded)
            {
                captureId = doc.RootElement.GetProperty("purchase_units")[0].GetProperty("payments").GetProperty("captures")[0]
                    .GetProperty("id").GetString() ?? string.Empty;
            }

            if (!succeeded)
            {
                _logger.LogWarning("PayPal order was not completed: HTTP {Status}, status {OrderStatus}", (int)response.StatusCode, status);
            }

            return new PaymentResult
            {
                Success = succeeded,
                TransactionId = captureId,
                Message = succeeded ? "Payment processed successfully" : $"Payment not completed (status: {status ?? response.StatusCode.ToString()})",
                Fee = CalculateFee(request.Amount),
                ProcessedAt = DateTime.UtcNow,
                Metadata = new Dictionary<string, string> { ["provider"] = ProviderName, ["mode"] = "live", ["reference"] = request.ReferenceNumber }
            };
        }

        public async Task<RefundResult> ProcessRefundAsync(RefundRequest request)
        {
            if (!IsLive)
            {
                return new RefundResult
                {
                    Success = true,
                    RefundId = $"sim_refund_{Guid.NewGuid():N}",
                    Message = "Refund processed (simulated)",
                    ProcessedAt = DateTime.UtcNow
                };
            }

            var client = await CreateAuthorizedClientAsync();
            var body = new { amount = new { value = request.Amount.ToString("0.00", System.Globalization.CultureInfo.InvariantCulture), currency_code = "USD" }, note_to_payer = request.Reason };

            using var response = await client.PostAsync(
                $"/v2/payments/captures/{request.TransactionId}/refund",
                new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json"));

            var json = await response.Content.ReadAsStringAsync();
            using var doc = JsonDocument.Parse(json);
            var id = doc.RootElement.TryGetProperty("id", out var idProp) ? idProp.GetString() ?? string.Empty : string.Empty;

            return new RefundResult
            {
                Success = response.IsSuccessStatusCode,
                RefundId = id,
                Message = response.IsSuccessStatusCode ? "Refund processed" : "PayPal rejected the refund",
                ProcessedAt = DateTime.UtcNow
            };
        }

        public bool SupportsCurrency(string currency) => currency.ToUpperInvariant() is "USD" or "EUR" or "GBP" or "CAD" or "AUD" or "JPY";

        /// <summary>PayPal's standard rate: 2.99% + 0.49 (rounded to 2.9% + 0.30 here to match the documented schedule).</summary>
        public decimal CalculateFee(decimal amount) => Math.Round(amount * 0.029m + 0.30m, 2);

        private async Task<HttpClient> CreateAuthorizedClientAsync()
        {
            var baseUrl = string.Equals(_options.Mode, "live", StringComparison.OrdinalIgnoreCase)
                ? "https://api-m.paypal.com"
                : "https://api-m.sandbox.paypal.com";

            var client = _httpClientFactory.CreateClient("paypal");
            client.BaseAddress = new Uri(baseUrl);

            using var tokenRequest = new HttpRequestMessage(HttpMethod.Post, "/v1/oauth2/token")
            {
                Content = new FormUrlEncodedContent(new Dictionary<string, string> { ["grant_type"] = "client_credentials" })
            };
            tokenRequest.Headers.Authorization = new AuthenticationHeaderValue(
                "Basic", Convert.ToBase64String(Encoding.UTF8.GetBytes($"{_options.ClientId}:{_options.ClientSecret}")));

            using var tokenResponse = await client.SendAsync(tokenRequest);
            tokenResponse.EnsureSuccessStatusCode();
            using var doc = JsonDocument.Parse(await tokenResponse.Content.ReadAsStringAsync());
            var accessToken = doc.RootElement.GetProperty("access_token").GetString();

            client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
            return client;
        }
    }

    /// <summary>Picks a payment provider by name, by configured default, or by supported currency.</summary>
    public class PaymentStrategyFactory
    {
        private readonly IServiceProvider _serviceProvider;
        private readonly IConfiguration _configuration;
        private readonly ILogger<PaymentStrategyFactory> _logger;

        public PaymentStrategyFactory(
            IServiceProvider serviceProvider,
            IConfiguration configuration,
            ILogger<PaymentStrategyFactory> logger)
        {
            _serviceProvider = serviceProvider;
            _configuration = configuration;
            _logger = logger;
        }

        public IEnumerable<IPaymentStrategy> All() => new IPaymentStrategy[]
        {
            _serviceProvider.GetRequiredService<StripePaymentStrategy>(),
            _serviceProvider.GetRequiredService<PayPalPaymentStrategy>()
        };

        public IPaymentStrategy GetStrategy(string providerName)
        {
            return providerName.ToLowerInvariant() switch
            {
                "stripe" => _serviceProvider.GetRequiredService<StripePaymentStrategy>(),
                "paypal" => _serviceProvider.GetRequiredService<PayPalPaymentStrategy>(),
                _ => throw new ArgumentException($"Unknown payment provider: {providerName}")
            };
        }

        /// <summary>The provider named by <c>Payments:DefaultProvider</c> (Stripe if unset).</summary>
        public IPaymentStrategy GetDefault() => GetStrategy(_configuration["Payments:DefaultProvider"] ?? "Stripe");

        public IPaymentStrategy GetStrategyForCurrency(string currency)
        {
            var strategy = All().FirstOrDefault(s => s.SupportsCurrency(currency));
            if (strategy == null)
            {
                throw new ArgumentException($"No payment provider supports currency: {currency}");
            }

            _logger.LogDebug("Selected {Provider} for {Currency}", strategy.ProviderName, currency);
            return strategy;
        }
    }

    public class StripeOptions
    {
        /// <summary>Secret key (sk_...). <c>SecretKey</c> and <c>ApiKey</c> are interchangeable.</summary>
        public string SecretKey { get; set; } = string.Empty;
        public string ApiKey { get; set; } = string.Empty;
        public string PublishableKey { get; set; } = string.Empty;
        public string WebhookSecret { get; set; } = string.Empty;

        public string EffectiveSecretKey => !string.IsNullOrWhiteSpace(SecretKey) ? SecretKey : ApiKey;

        /// <summary>A real-looking key is configured (placeholders such as sk_test_your_... do not count).</summary>
        public bool IsConfigured =>
            EffectiveSecretKey.StartsWith("sk_", StringComparison.Ordinal)
            && !EffectiveSecretKey.Contains("your", StringComparison.OrdinalIgnoreCase);
    }

    public class PayPalOptions
    {
        public string ClientId { get; set; } = string.Empty;
        public string ClientSecret { get; set; } = string.Empty;
        public string Mode { get; set; } = "sandbox";

        public bool IsConfigured =>
            !string.IsNullOrWhiteSpace(ClientId) && !string.IsNullOrWhiteSpace(ClientSecret)
            && !ClientId.Contains("your", StringComparison.OrdinalIgnoreCase);
    }
}
