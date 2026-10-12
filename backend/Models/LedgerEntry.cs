namespace BankingAPI.Models;

/// <summary>
/// Immutable double-entry style record of every balance movement on an account.
/// </summary>
public class LedgerEntry
{
    public Guid Id { get; set; }
    public Guid AccountId { get; set; }

    /// <summary>Null for reconciliation adjustments that are not tied to a transaction.</summary>
    public Guid? TransactionId { get; set; }

    public decimal Debit { get; set; }
    public decimal Credit { get; set; }
    public decimal BalanceAfter { get; set; }
    public string Description { get; set; } = string.Empty;
    public DateTime CreatedAt { get; set; }

    public Account Account { get; set; } = null!;
    public Transaction? Transaction { get; set; }
}
