namespace BankingAPI.Models;

/// <summary>
/// A tokenized payment instrument. Raw card numbers are never stored: the gateway returns a token,
/// and only the token, brand, last four digits and expiry are kept.
/// </summary>
public class PaymentMethod
{
    public Guid Id { get; set; }
    public Guid UserId { get; set; }

    /// <summary>Stripe, PayPal or Simulated.</summary>
    public string Provider { get; set; } = "Simulated";

    public string Token { get; set; } = string.Empty;
    public string Brand { get; set; } = string.Empty;
    public string Last4 { get; set; } = string.Empty;
    public int ExpMonth { get; set; }
    public int ExpYear { get; set; }
    public bool IsDefault { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime? RemovedAt { get; set; }

    public User User { get; set; } = null!;
}

public enum RecurrenceFrequency
{
    Daily,
    Weekly,
    Monthly
}

public enum RecurringStatus
{
    Active,
    Paused,
    Cancelled,
    Failed
}

/// <summary>A standing instruction to pay a merchant on a schedule from an account.</summary>
public class RecurringPayment
{
    public Guid Id { get; set; }
    public Guid AccountId { get; set; }
    public Guid UserId { get; set; }
    public string MerchantName { get; set; } = string.Empty;
    public decimal Amount { get; set; }
    public string Currency { get; set; } = "USD";
    public RecurrenceFrequency Frequency { get; set; }
    public DateTime NextRunAt { get; set; }
    public DateTime? EndAt { get; set; }
    public RecurringStatus Status { get; set; } = RecurringStatus.Active;
    public int ConsecutiveFailures { get; set; }
    public DateTime? LastRunAt { get; set; }
    public string? LastResult { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    public Account Account { get; set; } = null!;
}

public enum BankTransferRail
{
    Ach,
    Wire,
    Swift
}

public enum BankTransferStatus
{
    Submitted,
    Processing,
    Settled,
    Failed,
    Returned
}

/// <summary>An outgoing transfer over an external rail (ACH, domestic wire or international SWIFT).</summary>
public class BankTransfer
{
    public Guid Id { get; set; }
    public Guid AccountId { get; set; }
    public BankTransferRail Rail { get; set; }
    public decimal Amount { get; set; }
    public decimal Fee { get; set; }
    public string Currency { get; set; } = "USD";
    public string BeneficiaryName { get; set; } = string.Empty;
    public string? BeneficiaryAccountNumber { get; set; }
    public string? RoutingNumber { get; set; }
    public string? SwiftBic { get; set; }
    public string? Iban { get; set; }
    public string? DestinationCountry { get; set; }
    public string Reference { get; set; } = string.Empty;
    public BankTransferStatus Status { get; set; } = BankTransferStatus.Submitted;
    public string? FailureReason { get; set; }
    public Guid? DebitTransactionId { get; set; }
    public Guid? FeeTransactionId { get; set; }
    public DateTime SubmittedAt { get; set; } = DateTime.UtcNow;
    public DateTime ExpectedSettlementAt { get; set; }
    public DateTime? SettledAt { get; set; }

    public Account Account { get; set; } = null!;
}

public enum ChargebackStatus
{
    Opened,
    UnderReview,
    CustomerWon,
    MerchantWon
}

/// <summary>A cardholder dispute against a completed payment.</summary>
public class Chargeback
{
    public Guid Id { get; set; }
    public Guid PaymentLogId { get; set; }
    public Guid AccountId { get; set; }
    public decimal Amount { get; set; }
    public string Reason { get; set; } = string.Empty;
    public ChargebackStatus Status { get; set; } = ChargebackStatus.Opened;
    public Guid? ResolvedByUserId { get; set; }
    public string? ResolutionNotes { get; set; }
    public DateTime OpenedAt { get; set; } = DateTime.UtcNow;
    public DateTime? ResolvedAt { get; set; }

    public PaymentLog PaymentLog { get; set; } = null!;
    public Account Account { get; set; } = null!;
}
