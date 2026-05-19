namespace BankingAPI.Models;

public enum AccountType
{
    Savings,
    Checking,
    Business,
    FixedDeposit
}

public enum AccountStatus
{
    Active,
    Inactive,
    Frozen,
    Closed
}

public class Account
{
    public Guid Id { get; set; }
    public Guid UserId { get; set; }
    public string AccountNumber { get; set; } = string.Empty;
    public AccountType AccountType { get; set; }
    public decimal Balance { get; set; } = 0;
    public string Currency { get; set; } = "USD";
    public AccountStatus Status { get; set; } = AccountStatus.Active;
    public decimal OverdraftLimit { get; set; } = 0;
    public decimal InterestRate { get; set; } = 0;
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;

    // Navigation properties
    public User User { get; set; } = null!;
    public ICollection<Transaction> Transactions { get; set; } = new List<Transaction>();
}
