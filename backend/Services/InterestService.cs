using BankingAPI.Data;
using BankingAPI.Models;
using Microsoft.EntityFrameworkCore;

namespace BankingAPI.Services;

public interface IInterestService
{
    Task AccrueInterestAsync(Guid accountId);
    Task AccrueInterestForAllAccountsAsync();
    Task<decimal> CalculateDailyInterestAsync(Guid accountId);
}

public class InterestService : IInterestService
{
    /// <summary>Annual rate used when an account has none set (4.5%).</summary>
    public const decimal DefaultAnnualRate = 0.045m;

    private readonly ApplicationDbContext _context;
    private readonly ILedgerService _ledgerService;
    private readonly ILogger<InterestService>? _logger;

    public InterestService(ApplicationDbContext context, ILedgerService ledgerService, ILogger<InterestService>? logger = null)
    {
        _context = context;
        _ledgerService = ledgerService;
        _logger = logger;
    }

    public async Task AccrueInterestAsync(Guid accountId)
    {
        var account = await _context.Accounts.FindAsync(accountId)
            ?? throw new InvalidOperationException("Account not found.");

        var interest = ComputeDailyInterest(account);
        if (interest <= 0) return;

        // Interest is credited once per account per UTC day, so restarts and repeat runs are harmless.
        var today = DateTime.UtcNow.Date;
        var alreadyPaid = await _context.Transactions.AnyAsync(t =>
            t.AccountId == accountId && t.Type == TransactionType.Interest && t.CreatedAt >= today);
        if (alreadyPaid) return;

        var now = DateTime.UtcNow;
        var transaction = new Transaction
        {
            Id = Guid.NewGuid(),
            AccountId = accountId,
            Type = TransactionType.Interest,
            Amount = interest,
            Currency = account.Currency,
            BalanceBefore = account.Balance,
            Description = "Interest payment",
            ReferenceNumber = $"INT{now:yyyyMMddHHmmss}{Guid.NewGuid().ToString("N")[..8].ToUpperInvariant()}",
            Status = TransactionStatus.Completed,
            CreatedAt = now,
            UpdatedAt = now
        };

        account.Balance += interest;
        account.UpdatedAt = now;
        transaction.BalanceAfter = account.Balance;

        _context.Transactions.Add(transaction);
        await _context.SaveChangesAsync();
        await _ledgerService.RecordTransactionAsync(transaction);
    }

    public async Task AccrueInterestForAllAccountsAsync()
    {
        var accountIds = await _context.Accounts
            .Where(a => a.AccountType == AccountType.Savings && a.Status == AccountStatus.Active)
            .Select(a => a.Id)
            .ToListAsync();

        foreach (var accountId in accountIds)
        {
            try
            {
                await AccrueInterestAsync(accountId);
            }
            catch (Exception ex)
            {
                // One failing account must not stop interest for the others.
                _logger?.LogError(ex, "Interest accrual failed for account {AccountId}", accountId);
                _context.ChangeTracker.Clear();
            }
        }
    }

    public async Task<decimal> CalculateDailyInterestAsync(Guid accountId)
    {
        var account = await _context.Accounts.FindAsync(accountId)
            ?? throw new InvalidOperationException("Account not found.");

        return ComputeDailyInterest(account);
    }

    /// <summary>Daily interest on a positive savings balance, rounded to cents; 0 if below one cent.</summary>
    private static decimal ComputeDailyInterest(Account account)
    {
        if (account.AccountType != AccountType.Savings || account.Status != AccountStatus.Active || account.Balance <= 0)
        {
            return 0;
        }

        var annualRate = account.InterestRate > 0 ? account.InterestRate : DefaultAnnualRate;
        var interest = Math.Round(account.Balance * annualRate / 365m, 2, MidpointRounding.ToZero);
        return interest >= 0.01m ? interest : 0;
    }
}
