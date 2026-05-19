namespace BankingAPI.DTOs;

public class CreateAccountRequest
{
    public string AccountType { get; set; } = string.Empty;
    public string? Currency { get; set; }
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
