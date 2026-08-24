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
    private readonly ApplicationDbContext _context;
    private readonly ITransactionService _transactionService;

    public InterestService(ApplicationDbContext context, ITransactionService transactionService)
    {
        _context = context;
        _transactionService = transactionService;
    }

    public async Task AccrueInterestAsync(Guid accountId)
    {
        var account = await _context.Accounts.FindAsync(accountId);
        if (account == null)
        {
            throw new InvalidOperationException("Account not found.");
        }

        // Only accrue interest on savings accounts
        if (account.AccountType != AccountType.Savings)
        {
            return;
        }

        if (account.Status != AccountStatus.Active)
        {
            return;
        }

        // Calculate daily interest rate (4.5% annual / 365 days)
        var annualRate = account.InterestRate > 0 ? account.InterestRate : 0.045m;
        var dailyRate = annualRate / 365;
        var interest = account.Balance * dailyRate;

        // Only credit interest if it's meaningful (more than 0.01)
        if (interest < 0.01m)
        {
            return;
        }

        // Create interest transaction
        var transaction = new Transaction
        {
            Id = Guid.NewGuid(),
            AccountId = accountId,
            Type = TransactionType.Interest,
            Amount = interest,
            Currency = account.Currency,
            BalanceBefore = account.Balance,
            Description = "Interest payment",
            ReferenceNumber = GenerateInterestReferenceNumber(),
            Status = TransactionStatus.Completed,
            IpAddress = null
        };

        _context.Transactions.Add(transaction);

        // Update account balance
        account.Balance += interest;
        transaction.BalanceAfter = account.Balance;
        account.UpdatedAt = DateTime.UtcNow;
        transaction.UpdatedAt = DateTime.UtcNow;

        await _context.SaveChangesAsync();
    }

    public async Task AccrueInterestForAllAccountsAsync()
    {
        var savingsAccounts = await _context.Accounts
            .Where(a => a.AccountType == AccountType.Savings && a.Status == AccountStatus.Active)
            .ToListAsync();

        foreach (var account in savingsAccounts)
        {
            try
            {
                await AccrueInterestAsync(account.Id);
            }
            catch (Exception)
            {
                // Log error but continue with other accounts
                // In production, you'd want proper logging here
            }
        }
    }

    public async Task<decimal> CalculateDailyInterestAsync(Guid accountId)
    {
        var account = await _context.Accounts.FindAsync(accountId);
        if (account == null)
        {
            throw new InvalidOperationException("Account not found.");
        }

        if (account.AccountType != AccountType.Savings)
        {
            return 0;
        }

        var annualRate = account.InterestRate > 0 ? account.InterestRate : 0.045m;
        var dailyRate = annualRate / 365;
        return account.Balance * dailyRate;
    }

    private static string GenerateInterestReferenceNumber()
    {
        return $"INT{DateTime.UtcNow:yyyyMMddHHmmss}{new Random().Next(1000, 9999)}";
    }
}
