namespace BankingAPI.Models;

/// <summary>
/// The bank's own general-ledger accounts. Every customer posting is balanced by a counter-entry
/// here, so total debits always equal total credits.
/// </summary>
public enum GlAccount
{
    /// <summary>Asset. Cash held by the bank (debit-normal).</summary>
    Cash,

    /// <summary>Asset. Card payments captured by the gateway that the processor has yet to pay out to the bank (debit-normal).</summary>
    ProcessorReceivable,

    /// <summary>Liability. What the bank owes its customers: the sum of all account balances (credit-normal).</summary>
    CustomerDeposits,

    /// <summary>Liability. Counter-account for transfers between customers; the two legs cancel out.</summary>
    TransferClearing,

    /// <summary>Liability. Card/merchant payments owed to payment processors until settled.</summary>
    PaymentsClearing,

    /// <summary>Liability. ACH, wire and SWIFT transfers in flight until they settle.</summary>
    ExternalClearing,

    /// <summary>Expense. Interest paid to customers (debit-normal).</summary>
    InterestExpense,

    /// <summary>Income. Fees charged to customers (credit-normal).</summary>
    FeeIncome
}

public class GlEntry
{
    public Guid Id { get; set; }
    public Guid? TransactionId { get; set; }
    public GlAccount Account { get; set; }
    public decimal Debit { get; set; }
    public decimal Credit { get; set; }
    public string Currency { get; set; } = "USD";
    public string? Memo { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}
