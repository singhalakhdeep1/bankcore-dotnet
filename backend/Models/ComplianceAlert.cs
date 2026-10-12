namespace BankingAPI.Models;

public enum AlertStatus
{
    Open,
    UnderReview,
    Dismissed,
    Escalated
}

/// <summary>
/// Compliance / fraud alert raised against a transaction. <see cref="AlertType"/> is one of
/// LargeTransaction, HighFrequency, UnusualAmount, CurrencyReport (CTR), Structuring (AML),
/// SanctionsMatch (OFAC), KycRestriction, MultipleIps.
/// </summary>
public class ComplianceAlert
{
    public Guid Id { get; set; }
    public Guid AccountId { get; set; }
    public Guid? TransactionId { get; set; }
    public string AlertType { get; set; } = string.Empty;
    public string Severity { get; set; } = "Low";

    /// <summary>0-100 risk score contributed by this rule.</summary>
    public int RiskScore { get; set; }

    public string Description { get; set; } = string.Empty;
    public AlertStatus Status { get; set; } = AlertStatus.Open;
    public Guid? ReviewedByUserId { get; set; }
    public DateTime? ReviewedAt { get; set; }
    public string? ReviewNotes { get; set; }
    public DateTime CreatedAt { get; set; }

    public Account Account { get; set; } = null!;
    public Transaction? Transaction { get; set; }
}

/// <summary>An entry on a sanctions watch list (OFAC SDN style) used for screening counterparties.</summary>
public class SanctionedEntity
{
    public Guid Id { get; set; }
    public string Name { get; set; } = string.Empty;

    /// <summary>Lower-cased, punctuation-free form used for matching.</summary>
    public string NormalizedName { get; set; } = string.Empty;

    public string? AccountNumber { get; set; }
    public string? Country { get; set; }
    public string ListName { get; set; } = "OFAC-SDN";
    public bool IsActive { get; set; } = true;
    public DateTime AddedAt { get; set; } = DateTime.UtcNow;
}

public enum ReportType
{
    SuspiciousActivity,
    CurrencyTransaction,
    MonthlyCompliance
}

/// <summary>A generated regulatory report (SAR / CTR / monthly summary) stored as JSON.</summary>
public class RegulatoryReport
{
    public Guid Id { get; set; }
    public ReportType Type { get; set; }
    public DateTime PeriodStart { get; set; }
    public DateTime PeriodEnd { get; set; }
    public int ItemCount { get; set; }
    public string PayloadJson { get; set; } = "{}";
    public Guid GeneratedByUserId { get; set; }
    public DateTime GeneratedAt { get; set; } = DateTime.UtcNow;
}
