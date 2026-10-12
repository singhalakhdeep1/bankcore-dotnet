using BankCore.Services.Caching;
using BankingAPI.Data;
using BankingAPI.DTOs;
using BankingAPI.Models;
using BankingAPI.Services.Caching;
using BankingAPI.Services.Security;
using Microsoft.EntityFrameworkCore;

namespace BankingAPI.Services;

public interface IAccountService
{
    Task<List<AccountDto>> GetUserAccountsAsync(Guid userId);
    Task<AccountDto?> GetAccountByIdAsync(Guid accountId, Guid userId);
    Task<AccountDto?> CreateAccountAsync(Guid userId, CreateAccountRequest request);
    Task<Guid?> GetAccountIdByNumberAsync(string accountNumber);

    /// <summary>Closes (soft-deletes) an account with a zero balance. Returns an error message, or null on success.</summary>
    Task<string?> CloseAccountAsync(Guid accountId, Guid userId);
}

public class AccountService : IAccountService
{
    private readonly ApplicationDbContext _context;
    private readonly DistributedCacheService? _cache;
    private readonly IAccountCache? _accountCache;
    private readonly IAuditService? _audit;

    public AccountService(
        ApplicationDbContext context,
        DistributedCacheService? cache = null,
        IAccountCache? accountCache = null,
        IAuditService? audit = null)
    {
        _context = context;
        _cache = cache;
        _accountCache = accountCache;
        _audit = audit;
    }

    public async Task<List<AccountDto>> GetUserAccountsAsync(Guid userId)
    {
        async Task<List<AccountDto>> Load()
        {
            var accounts = await _context.Accounts
                .AsNoTracking()
                .Where(a => a.UserId == userId)
                .OrderBy(a => a.CreatedAt)
                .ToListAsync();

            return accounts.Select(MapToAccountDto).ToList();
        }

        if (_cache == null || _accountCache == null) return await Load();

        // Cached per user; any posting on one of the user's accounts bumps the version and so skips stale entries.
        var version = await _accountCache.UserVersionAsync(userId);
        return await _cache.GetOrSetAsync($"accounts:{userId}:{version}", Load, TimeSpan.FromSeconds(60));
    }

    public async Task<AccountDto?> GetAccountByIdAsync(Guid accountId, Guid userId)
    {
        // Balances must be exact before money moves, so single-account reads always hit the database.
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

        if (_accountCache != null) await _accountCache.BumpAsync(account.Id, userId);
        if (_audit != null) await _audit.LogAsync("Account.Opened", userId, "Account", account.Id.ToString(), $"{accountType} {account.Currency}");

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

    public async Task<string?> CloseAccountAsync(Guid accountId, Guid userId)
    {
        var account = await _context.Accounts.FirstOrDefaultAsync(a => a.Id == accountId && a.UserId == userId);
        if (account == null) return "Account not found";
        if (account.Balance != 0) return "Withdraw or transfer the remaining balance before closing the account.";

        var pendingTransfers = await _context.BankTransfers.AnyAsync(t =>
            t.AccountId == accountId && (t.Status == BankTransferStatus.Submitted || t.Status == BankTransferStatus.Processing));
        if (pendingTransfers) return "Wait for pending bank transfers to settle first.";

        // Standing instructions on a closed account would only fail, so they are cancelled with it.
        var recurring = await _context.RecurringPayments
            .Where(p => p.AccountId == accountId && (p.Status == RecurringStatus.Active || p.Status == RecurringStatus.Paused))
            .ToListAsync();
        foreach (var payment in recurring) payment.Status = RecurringStatus.Cancelled;

        account.Status = AccountStatus.Closed;
        account.IsDeleted = true;
        account.DeletedAt = DateTime.UtcNow;
        await _context.SaveChangesAsync();

        if (_accountCache != null) await _accountCache.BumpAsync(accountId, userId);
        if (_audit != null) await _audit.LogAsync("Account.Closed", userId, "Account", accountId.ToString());
        return null;
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
