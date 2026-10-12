namespace BankingAPI.Models;

public enum LedgerPeriodStatus
{
    Open,
    Closed
}

/// <summary>
/// An accounting period. Closing a period freezes a snapshot of every account's balance and totals,
/// and prevents posting entries dated inside it.
/// </summary>
public class LedgerPeriod
{
    public Guid Id { get; set; }
    public string Name { get; set; } = string.Empty;
    public DateTime StartDate { get; set; }
    public DateTime EndDate { get; set; }
    public LedgerPeriodStatus Status { get; set; } = LedgerPeriodStatus.Open;
    public decimal TotalDebits { get; set; }
    public decimal TotalCredits { get; set; }
    public Guid? ClosedByUserId { get; set; }
    public DateTime? ClosedAt { get; set; }

    public ICollection<PeriodBalance> Balances { get; set; } = new List<PeriodBalance>();
}

/// <summary>One account's totals and closing balance at the end of a closed period.</summary>
public class PeriodBalance
{
    public Guid Id { get; set; }
    public Guid PeriodId { get; set; }
    public Guid AccountId { get; set; }
    public string Currency { get; set; } = "USD";
    public decimal Debits { get; set; }
    public decimal Credits { get; set; }
    public decimal ClosingBalance { get; set; }

    public LedgerPeriod Period { get; set; } = null!;
}
