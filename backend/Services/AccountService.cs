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
    Task<Guid?> GetAccountIdByNumberAsync(string accountNumber);
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
            .AsNoTracking()
            .Where(a => a.UserId == userId)
            .OrderBy(a => a.CreatedAt)
            .ToListAsync();

        return accounts.Select(MapToAccountDto).ToList();
    }

    public async Task<AccountDto?> GetAccountByIdAsync(Guid accountId, Guid userId)
    {
        var account = await _context.Accounts
            .AsNoTracking()
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
            Currency = (request.Currency ?? "USD").ToUpperInvariant(),
            Status = AccountStatus.Active,
            InterestRate = accountType == AccountType.Savings ? InterestService.DefaultAnnualRate : 0
        };

        _context.Accounts.Add(account);
        await _context.SaveChangesAsync();

        return MapToAccountDto(account);
    }

    public async Task<Guid?> GetAccountIdByNumberAsync(string accountNumber)
    {
        var number = accountNumber.Trim().ToUpperInvariant();
        return await _context.Accounts
            .AsNoTracking()
            .Where(a => a.AccountNumber == number)
            .Select(a => (Guid?)a.Id)
            .FirstOrDefaultAsync();
    }

    private static string GenerateAccountNumber()
    {
        return Guid.NewGuid().ToString("N")[..20].ToUpperInvariant();
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
            InterestRate = account.InterestRate,
            CreatedAt = account.CreatedAt
        };
    }
}
