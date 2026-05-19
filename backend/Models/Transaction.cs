namespace BankingAPI.Models;

public enum TransactionType
{
    Deposit,
    Withdrawal,
    Transfer,
    Payment,
    Refund,
    Interest,
    Fee,
    Reversal
}

public enum TransactionStatus
{
    Pending,
    Completed,
    Failed,
    Reversed
}

public class Transaction
{
    public Guid Id { get; set; }
    public Guid AccountId { get; set; }
    public TransactionType Type { get; set; }
    public decimal Amount { get; set; }
    public string Currency { get; set; } = "USD";
    public decimal BalanceBefore { get; set; }
    public decimal BalanceAfter { get; set; }
    public TransactionStatus Status { get; set; } = TransactionStatus.Pending;
    public string? Description { get; set; }
    public string ReferenceNumber { get; set; } = string.Empty;
    public string? ToAccountNumber { get; set; }
    public string? FromAccountNumber { get; set; }
    public bool IsFraudulent { get; set; } = false;
    public string? IpAddress { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;

    // Navigation properties
    public Account Account { get; set; } = null!;
}
