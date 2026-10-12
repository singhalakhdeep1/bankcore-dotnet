using System.ComponentModel.DataAnnotations;
using BankingAPI.Models;

namespace BankingAPI.DTOs;

public class CreateAccountRequest
{
    [Required]
    public string AccountType { get; set; } = string.Empty;

    [RegularExpression("^[A-Za-z]{3}$", ErrorMessage = "Currency must be a 3-letter ISO code")]
    public string? Currency { get; set; }
}

public class DepositRequest
{
    [Range(0.01, 1_000_000_000)]
    public decimal Amount { get; set; }

    [MaxLength(200)]
    public string? Description { get; set; }
}

public class WithdrawRequest
{
    [Range(0.01, 1_000_000_000)]
    public decimal Amount { get; set; }

    [MaxLength(200)]
    public string? Description { get; set; }
}

public class TransferRequest
{
    public Guid FromAccountId { get; set; }

    /// <summary>Destination account id, or use <see cref="ToAccountNumber"/>.</summary>
    public Guid? ToAccountId { get; set; }

    /// <summary>Destination account number, which is what customers actually know.</summary>
    [MaxLength(50)]
    public string? ToAccountNumber { get; set; }

    [Range(0.01, 1_000_000_000)]
    public decimal Amount { get; set; }

    [MaxLength(200)]
    public string? Description { get; set; }

    /// <summary>Optional client-generated key; repeating a request with the same key will not move money twice.</summary>
    [MaxLength(64)]
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
    public decimal InterestRate { get; set; }
    public DateTime CreatedAt { get; set; }
}

public class TransactionDto
{
    public Guid Id { get; set; }
    public Guid AccountId { get; set; }
    public string Type { get; set; } = string.Empty;
    public decimal Amount { get; set; }
    public string Currency { get; set; } = string.Empty;
    public decimal BalanceBefore { get; set; }
    public decimal BalanceAfter { get; set; }
    public string Status { get; set; } = string.Empty;
    public string? Description { get; set; }
    public string ReferenceNumber { get; set; } = string.Empty;
    public string? ToAccountNumber { get; set; }
    public string? FromAccountNumber { get; set; }
    public DateTime CreatedAt { get; set; }

    public static TransactionDto From(Transaction t) => new()
    {
        Id = t.Id,
        AccountId = t.AccountId,
        Type = t.Type.ToString(),
        Amount = t.Amount,
        Currency = t.Currency,
        BalanceBefore = t.BalanceBefore,
        BalanceAfter = t.BalanceAfter,
        Status = t.Status.ToString(),
        Description = t.Description,
        ReferenceNumber = t.ReferenceNumber,
        ToAccountNumber = t.ToAccountNumber,
        FromAccountNumber = t.FromAccountNumber,
        CreatedAt = t.CreatedAt
    };
}

public class TransferResultDto
{
    public TransactionDto From { get; set; } = null!;
    public TransactionDto To { get; set; } = null!;
}

public class PagedResult<T>
{
    public List<T> Items { get; set; } = new();
    public int Page { get; set; }
    public int PageSize { get; set; }
    public int TotalCount { get; set; }
    public int TotalPages => PageSize == 0 ? 0 : (int)Math.Ceiling(TotalCount / (double)PageSize);
}

public class PaymentRequestDto
{
    public Guid AccountId { get; set; }

    [Range(0.01, 1_000_000_000)]
    public decimal Amount { get; set; }

    [Required, MaxLength(200)]
    public string MerchantName { get; set; } = string.Empty;

    [MaxLength(200)]
    public string? Description { get; set; }
}

public class RefundRequestDto
{
    /// <summary>Omit for a full refund.</summary>
    [Range(0.01, 1_000_000_000)]
    public decimal? Amount { get; set; }
}
