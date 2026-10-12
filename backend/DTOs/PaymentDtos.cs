using System.ComponentModel.DataAnnotations;
using BankingAPI.Models;

namespace BankingAPI.DTOs;

public class AddPaymentMethodRequest
{
    /// <summary>Stripe or PayPal.</summary>
    [Required]
    public string Provider { get; set; } = "Stripe";

    /// <summary>Token created by the provider's client SDK (Stripe.js pm_..., PayPal vault id). Required for live gateways.</summary>
    public string? ProviderToken { get; set; }

    /// <summary>Raw card details are only accepted by the simulated gateway and are never stored or logged.</summary>
    public string? CardNumber { get; set; }

    [Range(1, 12)]
    public int ExpMonth { get; set; }

    [Range(2000, 2100)]
    public int ExpYear { get; set; }

    [StringLength(4, MinimumLength = 3)]
    public string? Cvc { get; set; }

    public bool MakeDefault { get; set; }
}

public class PaymentMethodDto
{
    public Guid Id { get; set; }
    public string Provider { get; set; } = string.Empty;
    public string Brand { get; set; } = string.Empty;
    public string Last4 { get; set; } = string.Empty;
    public int ExpMonth { get; set; }
    public int ExpYear { get; set; }
    public bool IsDefault { get; set; }
    public DateTime CreatedAt { get; set; }

    public static PaymentMethodDto From(PaymentMethod m) => new()
    {
        Id = m.Id,
        Provider = m.Provider,
        Brand = m.Brand,
        Last4 = m.Last4,
        ExpMonth = m.ExpMonth,
        ExpYear = m.ExpYear,
        IsDefault = m.IsDefault,
        CreatedAt = m.CreatedAt
    };
}

public class CardTopUpDto
{
    public Guid AccountId { get; set; }
    public Guid PaymentMethodId { get; set; }

    [Range(0.01, 1_000_000)]
    public decimal Amount { get; set; }

    [MaxLength(200)]
    public string? Description { get; set; }
}

public class CreateRecurringPaymentRequest
{
    public Guid AccountId { get; set; }

    [Required, MaxLength(200)]
    public string MerchantName { get; set; } = string.Empty;

    [Range(0.01, 1_000_000)]
    public decimal Amount { get; set; }

    public RecurrenceFrequency Frequency { get; set; } = RecurrenceFrequency.Monthly;

    /// <summary>First payment date (UTC). Defaults to now.</summary>
    public DateTime? StartAt { get; set; }

    public DateTime? EndAt { get; set; }
}

public class BankTransferRequest
{
    public Guid AccountId { get; set; }
    public BankTransferRail Rail { get; set; }

    [Range(0.01, 1_000_000)]
    public decimal Amount { get; set; }

    [Required, MaxLength(200)]
    public string BeneficiaryName { get; set; } = string.Empty;

    /// <summary>Beneficiary bank account number (ACH, wire, or SWIFT without an IBAN).</summary>
    [MaxLength(34)]
    public string? BeneficiaryAccountNumber { get; set; }

    /// <summary>9-digit ABA routing number (ACH and domestic wire).</summary>
    [MaxLength(9)]
    public string? RoutingNumber { get; set; }

    /// <summary>8 or 11 character BIC (SWIFT).</summary>
    [MaxLength(11)]
    public string? SwiftBic { get; set; }

    [MaxLength(34)]
    public string? Iban { get; set; }

    /// <summary>2-letter ISO country (SWIFT).</summary>
    [MaxLength(2)]
    public string? DestinationCountry { get; set; }

    [MaxLength(140)]
    public string? Memo { get; set; }
}

public class ChargebackRequest
{
    [Required, MaxLength(500)]
    public string Reason { get; set; } = string.Empty;
}

public class ResolveChargebackRequest
{
    public ChargebackStatus Outcome { get; set; }

    [MaxLength(1000)]
    public string? Notes { get; set; }
}

public class BankTransferDto
{
    public Guid Id { get; set; }
    public Guid AccountId { get; set; }
    public string Rail { get; set; } = string.Empty;
    public decimal Amount { get; set; }
    public decimal Fee { get; set; }
    public string Currency { get; set; } = string.Empty;
    public string BeneficiaryName { get; set; } = string.Empty;
    public string? BeneficiaryAccountNumber { get; set; }
    public string? RoutingNumber { get; set; }
    public string? SwiftBic { get; set; }
    public string? Iban { get; set; }
    public string? DestinationCountry { get; set; }
    public string Reference { get; set; } = string.Empty;
    public string Status { get; set; } = string.Empty;
    public string? FailureReason { get; set; }
    public DateTime SubmittedAt { get; set; }
    public DateTime ExpectedSettlementAt { get; set; }
    public DateTime? SettledAt { get; set; }

    public static BankTransferDto From(BankTransfer t) => new()
    {
        Id = t.Id,
        AccountId = t.AccountId,
        Rail = t.Rail.ToString(),
        Amount = t.Amount,
        Fee = t.Fee,
        Currency = t.Currency,
        BeneficiaryName = t.BeneficiaryName,
        // Only the last four digits of the beneficiary account are echoed back.
        BeneficiaryAccountNumber = Mask(t.BeneficiaryAccountNumber),
        RoutingNumber = t.RoutingNumber,
        SwiftBic = t.SwiftBic,
        Iban = Mask(t.Iban),
        DestinationCountry = t.DestinationCountry,
        Reference = t.Reference,
        Status = t.Status.ToString(),
        FailureReason = t.FailureReason,
        SubmittedAt = t.SubmittedAt,
        ExpectedSettlementAt = t.ExpectedSettlementAt,
        SettledAt = t.SettledAt
    };

    private static string? Mask(string? value) =>
        string.IsNullOrEmpty(value) ? value : value.Length <= 4 ? value : new string('*', value.Length - 4) + value[^4..];
}

public class RecurringPaymentDto
{
    public Guid Id { get; set; }
    public Guid AccountId { get; set; }
    public string MerchantName { get; set; } = string.Empty;
    public decimal Amount { get; set; }
    public string Currency { get; set; } = string.Empty;
    public string Frequency { get; set; } = string.Empty;
    public DateTime NextRunAt { get; set; }
    public DateTime? EndAt { get; set; }
    public string Status { get; set; } = string.Empty;
    public int ConsecutiveFailures { get; set; }
    public DateTime? LastRunAt { get; set; }
    public string? LastResult { get; set; }
    public DateTime CreatedAt { get; set; }

    public static RecurringPaymentDto From(RecurringPayment p) => new()
    {
        Id = p.Id,
        AccountId = p.AccountId,
        MerchantName = p.MerchantName,
        Amount = p.Amount,
        Currency = p.Currency,
        Frequency = p.Frequency.ToString(),
        NextRunAt = p.NextRunAt,
        EndAt = p.EndAt,
        Status = p.Status.ToString(),
        ConsecutiveFailures = p.ConsecutiveFailures,
        LastRunAt = p.LastRunAt,
        LastResult = p.LastResult,
        CreatedAt = p.CreatedAt
    };
}
