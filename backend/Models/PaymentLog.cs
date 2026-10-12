namespace BankingAPI.Models;

/// <summary>
/// Audit record for a payment sent through the (simulated) payment gateway.
/// </summary>
public class PaymentLog
{
    public Guid Id { get; set; }
    public Guid AccountId { get; set; }

    /// <summary>Public payment reference, e.g. PAY20260101120000ab12cd34.</summary>
    public string TransactionId { get; set; } = string.Empty;

    /// <summary>The ledger transaction that debited the account, used for refunds.</summary>
    public Guid? LedgerTransactionId { get; set; }

    public string? GatewayTransactionId { get; set; }

    /// <summary>MerchantPayment debits the account to pay a merchant; CardTopUp credits it by charging a card.</summary>
    public string Kind { get; set; } = "MerchantPayment";

    /// <summary>Gateway used for a card top-up (Stripe or PayPal); null for merchant payments.</summary>
    public string? Provider { get; set; }

    /// <summary>live or simulated: whether the gateway call really reached Stripe/PayPal.</summary>
    public string? ProviderMode { get; set; }

    public Guid? PaymentMethodId { get; set; }

    /// <summary>Processing cost charged by the gateway.</summary>
    public decimal Fee { get; set; }
    public decimal Amount { get; set; }
    public string Currency { get; set; } = "USD";
    public string MerchantName { get; set; } = string.Empty;
    public string Status { get; set; } = "Pending";
    public decimal? RefundAmount { get; set; }
    public string? ErrorMessage { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }

    public Account Account { get; set; } = null!;
}
