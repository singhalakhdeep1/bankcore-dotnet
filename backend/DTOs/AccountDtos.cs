namespace BankingAPI.DTOs;

public class CreateAccountRequest
{
    public string AccountType { get; set; } = string.Empty;
    public string? Currency { get; set; }
}

public class DepositRequest
{
    public decimal Amount { get; set; }
    public string? Description { get; set; }
}

public class WithdrawRequest
{
    public decimal Amount { get; set; }
    public string? Description { get; set; }
}

public class TransferRequest
{
    public Guid FromAccountId { get; set; }
    public Guid ToAccountId { get; set; }
    public decimal Amount { get; set; }
    public string? Description { get; set; }
    public string? IdempotencyKey { get; set; }
}

public class AccountDto
{
    public Guid Id { get; set; }
    public string AccountNumber { get; set; } = string.Empty;
    public string AccountType { get; set; } = string.Empty;
    public decimal Balance { get; set; }
    public string Currency { get; set; } = string.Empty;
    public string Status { get; set; } = string.Empty;
    public DateTime CreatedAt { get; set; }
}
