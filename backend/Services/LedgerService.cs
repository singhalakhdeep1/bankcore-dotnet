using BankingAPI.Data;
using BankingAPI.Models;
using Microsoft.EntityFrameworkCore;

namespace BankingAPI.Services;

public interface ILedgerService
{
    Task RecordTransactionAsync(Transaction transaction);
    Task<decimal> GetAccountBalanceAsync(Guid accountId);
    Task<List<LedgerEntry>> GetAccountLedgerAsync(Guid accountId);
    Task<bool> ReconcileAccountAsync(Guid accountId);
}

public class LedgerService : ILedgerService
{
    private readonly ApplicationDbContext _context;

    public LedgerService(ApplicationDbContext context)
    {
        _context = context;
    }

    public async Task RecordTransactionAsync(Transaction transaction)
    {
        var ledgerEntry = new LedgerEntry
        {
            Id = Guid.NewGuid(),
            AccountId = transaction.AccountId,
            TransactionId = transaction.Id,
            Debit = transaction.Amount < 0 ? Math.Abs(transaction.Amount) : 0,
            Credit = transaction.Amount > 0 ? transaction.Amount : 0,
            BalanceAfter = transaction.BalanceAfter,
            Description = transaction.Description ?? transaction.Type.ToString(),
            CreatedAt = DateTime.UtcNow
        };

        _context.LedgerEntries.Add(ledgerEntry);
        await _context.SaveChangesAsync();
    }

    public async Task<decimal> GetAccountBalanceAsync(Guid accountId)
    {
        var latestEntry = await _context.LedgerEntries
            .Where(le => le.AccountId == accountId)
            .OrderByDescending(le => le.CreatedAt)
            .FirstOrDefaultAsync();

        return latestEntry?.BalanceAfter ?? 0;
    }

    public async Task<List<LedgerEntry>> GetAccountLedgerAsync(Guid accountId)
    {
        return await _context.LedgerEntries
            .Where(le => le.AccountId == accountId)
            .OrderByDescending(le => le.CreatedAt)
            .ToListAsync();
    }

    public async Task<bool> ReconcileAccountAsync(Guid accountId)
    {
        var account = await _context.Accounts.FindAsync(accountId);
        if (account == null) return false;

        // Calculate balance from ledger
        var ledgerBalance = await _context.LedgerEntries
            .Where(le => le.AccountId == accountId)
            .SumAsync(le => le.Credit - le.Debit);

        // Compare with account balance
        var isReconciled = Math.Abs(account.Balance - ledgerBalance) < 0.01m;

        if (!isReconciled)
        {
            // Create reconciliation entry
            var adjustment = account.Balance - ledgerBalance;
            var reconciliationEntry = new LedgerEntry
            {
                Id = Guid.NewGuid(),
                AccountId = accountId,
                TransactionId = Guid.Empty,
                Debit = adjustment < 0 ? Math.Abs(adjustment) : 0,
                Credit = adjustment > 0 ? adjustment : 0,
                BalanceAfter = account.Balance,
                Description = "Reconciliation Adjustment",
                CreatedAt = DateTime.UtcNow
            };

            _context.LedgerEntries.Add(reconciliationEntry);
            await _context.SaveChangesAsync();
        }

        return isReconciled;
    }
}

public class LedgerEntry
{
    public Guid Id { get; set; }
    public Guid AccountId { get; set; }
    public Guid TransactionId { get; set; }
    public decimal Debit { get; set; }
    public decimal Credit { get; set; }
    public decimal BalanceAfter { get; set; }
    public string Description { get; set; } = string.Empty;
    public DateTime CreatedAt { get; set; }

    // Navigation properties
    public Account Account { get; set; } = null!;
    public Transaction? Transaction { get; set; }
}
