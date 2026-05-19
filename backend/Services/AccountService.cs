using BankingAPI.Data;
using BankingAPI.Models;
using BankingAPI.DTOs;
using Microsoft.EntityFrameworkCore;

namespace BankingAPI.Services;

public interface IAccountService
{
    Task<List<AccountDto>> GetUserAccountsAsync(Guid userId);
    Task<AccountDto?> GetAccountByIdAsync(Guid accountId, Guid userId);
    Task<AccountDto?> CreateAccountAsync(Guid userId, CreateAccountRequest request);
}

public class AccountService : IAccountService
{
    private readonly ApplicationDbContext _context;

    public AccountService(ApplicationDbContext context)
    {
        _context = context;
    }

    public async Task<List<AccountDto>> GetUserAccountsAsync(Guid userId)
    {
        var accounts = await _context.Accounts
            .Where(a => a.UserId == userId)
            .ToListAsync();

        return accounts.Select(MapToAccountDto).ToList();
    }

    public async Task<AccountDto?> GetAccountByIdAsync(Guid accountId, Guid userId)
    {
        var account = await _context.Accounts
            .FirstOrDefaultAsync(a => a.Id == accountId && a.UserId == userId);

        return account != null ? MapToAccountDto(account) : null;
    }

    public async Task<AccountDto?> CreateAccountAsync(Guid userId, CreateAccountRequest request)
    {
        if (!Enum.TryParse<AccountType>(request.AccountType, true, out var accountType))
        {
            return null;
        }

        var account = new Account
        {
            Id = Guid.NewGuid(),
            UserId = userId,
            AccountNumber = GenerateAccountNumber(),
            AccountType = accountType,
            Balance = 0,
            Currency = request.Currency ?? "USD",
            Status = AccountStatus.Active
        };

        _context.Accounts.Add(account);
        await _context.SaveChangesAsync();

        return MapToAccountDto(account);
    }

    private static string GenerateAccountNumber()
    {
        return Guid.NewGuid().ToString("N")[..20].ToUpper();
    }

    private static AccountDto MapToAccountDto(Account account)
    {
        return new AccountDto
        {
            Id = account.Id,
            AccountNumber = account.AccountNumber,
            AccountType = account.AccountType.ToString(),
            Balance = account.Balance,
            Currency = account.Currency,
            Status = account.Status.ToString(),
            CreatedAt = account.CreatedAt
        };
    }
}
