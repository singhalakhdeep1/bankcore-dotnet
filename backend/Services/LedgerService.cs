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

    /// <summary>
    /// Writes one ledger entry for a completed transaction. Amounts on transactions are signed
    /// (credits positive, debits negative), so the sign decides whether it is a debit or a credit.
    /// The caller must have set <see cref="Transaction.BalanceAfter"/> already.
    /// </summary>
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

    /// <summary>Balance derived purely from the ledger (sum of credits minus debits).</summary>
    public async Task<decimal> GetAccountBalanceAsync(Guid accountId)
    {
        return await _context.LedgerEntries
            .Where(le => le.AccountId == accountId)
            .SumAsync(le => le.Credit - le.Debit);
    }

    public async Task<List<LedgerEntry>> GetAccountLedgerAsync(Guid accountId)
    {
        return await _context.LedgerEntries
            .Where(le => le.AccountId == accountId)
            .OrderByDescending(le => le.CreatedAt)
            .ToListAsync();
    }

    /// <summary>
    /// Checks that the stored account balance equals the ledger-derived balance.
    /// This is a read-only check: a mismatch is reported, never silently "fixed".
    /// </summary>
    public async Task<bool> ReconcileAccountAsync(Guid accountId)
    {
        var account = await _context.Accounts.FindAsync(accountId);
        if (account == null) return false;

        var ledgerBalance = await GetAccountBalanceAsync(accountId);
        return Math.Abs(account.Balance - ledgerBalance) < 0.01m;
    }
}
