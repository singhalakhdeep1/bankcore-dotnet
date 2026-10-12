using BankingAPI.Data;
using BankingAPI.Extensions;
using BankingAPI.Models;
using Microsoft.EntityFrameworkCore;

namespace BankingAPI.Services;

public interface ILedgerService
{
    Task RecordTransactionAsync(Transaction transaction);
    Task RecordSettlementAsync(Guid referenceId, decimal amount, string currency, string memo, bool reverse = false);
    Task<decimal> GetAccountBalanceAsync(Guid accountId);
    Task<List<LedgerEntry>> GetAccountLedgerAsync(Guid accountId);
    Task<bool> ReconcileAccountAsync(Guid accountId);
    Task<ReconciliationReport> ReconcileAllAsync();
    Task<TrialBalance> GetTrialBalanceAsync(DateTime? asOf = null);
    Task<BalanceSheet> GetBalanceSheetAsync(DateTime? asOf = null);
    Task<LedgerPeriod> ClosePeriodAsync(string name, DateTime start, DateTime end, Guid closedByUserId);
    Task<List<LedgerPeriod>> GetPeriodsAsync();
}

public class TrialBalanceLine
{
    public string Account { get; set; } = string.Empty;
    public string Currency { get; set; } = string.Empty;
    public decimal Debits { get; set; }
    public decimal Credits { get; set; }

    /// <summary>Debits minus credits.</summary>
    public decimal Net { get; set; }
}

public class TrialBalanceTotal
{
    public string Currency { get; set; } = string.Empty;
    public decimal Debits { get; set; }
    public decimal Credits { get; set; }
    public bool Balanced { get; set; }
}

public class TrialBalance
{
    public DateTime AsOf { get; set; }
    public List<TrialBalanceLine> Lines { get; set; } = new();
    public List<TrialBalanceTotal> Totals { get; set; } = new();
    public bool Balanced => Totals.All(t => t.Balanced);
}

public class BalanceSheetSection
{
    public string Currency { get; set; } = string.Empty;
    public Dictionary<string, decimal> Assets { get; set; } = new();
    public Dictionary<string, decimal> Liabilities { get; set; } = new();

    /// <summary>Fee income minus interest expense.</summary>
    public decimal RetainedEarnings { get; set; }

    public decimal TotalAssets { get; set; }
    public decimal TotalLiabilities { get; set; }

    /// <summary>Assets = Liabilities + Equity.</summary>
    public bool Balanced { get; set; }
}

public class BalanceSheet
{
    public DateTime AsOf { get; set; }
    public List<BalanceSheetSection> Sections { get; set; } = new();
}

public class ReconciliationMismatch
{
    public Guid AccountId { get; set; }
    public string AccountNumber { get; set; } = string.Empty;
    public decimal StoredBalance { get; set; }
    public decimal LedgerBalance { get; set; }
}

public class ReconciliationReport
{
    public DateTime RunAt { get; set; } = DateTime.UtcNow;
    public int AccountsChecked { get; set; }
    public List<ReconciliationMismatch> Mismatches { get; set; } = new();
    public bool Clean => Mismatches.Count == 0;
}

/// <summary>
/// Double-entry ledger. Each posting writes a customer ledger entry plus balanced general-ledger
/// entries (customer deposits against cash / a clearing account), so the trial balance always nets to zero.
/// </summary>
public class LedgerService : ILedgerService
{
    private readonly ApplicationDbContext _context;

    public LedgerService(ApplicationDbContext context)
    {
        _context = context;
    }

    /// <summary>
    /// Writes one customer ledger entry and its balanced GL entries for a completed transaction.
    /// Transaction amounts are signed (credits positive, debits negative).
    /// The caller must have set <see cref="Transaction.BalanceAfter"/> already.
    /// </summary>
    public async Task RecordTransactionAsync(Transaction transaction)
    {
        await EnsurePeriodOpenAsync(transaction.CreatedAt);

        var absolute = Math.Abs(transaction.Amount);
        var isCredit = transaction.Amount > 0;
        var now = DateTime.UtcNow;

        _context.LedgerEntries.Add(new LedgerEntry
        {
            Id = Guid.NewGuid(),
            AccountId = transaction.AccountId,
            TransactionId = transaction.Id,
            Debit = isCredit ? 0 : absolute,
            Credit = isCredit ? absolute : 0,
            BalanceAfter = transaction.BalanceAfter,
            Description = transaction.Description ?? transaction.Type.ToString(),
            CreatedAt = now
        });

        // The customer's balance is a liability of the bank: crediting the customer credits CustomerDeposits.
        var counter = transaction.GlCounter ?? DefaultCounter(transaction.Type);
        var memo = transaction.Description ?? transaction.Type.ToString();

        _context.GlEntries.Add(new GlEntry
        {
            Id = Guid.NewGuid(),
            TransactionId = transaction.Id,
            Account = GlAccount.CustomerDeposits,
            Debit = isCredit ? 0 : absolute,
            Credit = isCredit ? absolute : 0,
            Currency = transaction.Currency,
            Memo = memo,
            CreatedAt = now
        });

        _context.GlEntries.Add(new GlEntry
        {
            Id = Guid.NewGuid(),
            TransactionId = transaction.Id,
            Account = counter,
            Debit = isCredit ? absolute : 0,
            Credit = isCredit ? 0 : absolute,
            Currency = transaction.Currency,
            Memo = memo,
            CreatedAt = now
        });

        await _context.SaveChangesAsync();
    }

    /// <summary>
    /// An external transfer left the bank: the in-flight liability is cleared against cash.
    /// With <paramref name="reverse"/> (the beneficiary bank returned the money) the entry is undone.
    /// </summary>
    public async Task RecordSettlementAsync(Guid referenceId, decimal amount, string currency, string memo, bool reverse = false)
    {
        var now = DateTime.UtcNow;
        _context.GlEntries.Add(new GlEntry
        {
            Id = Guid.NewGuid(), TransactionId = referenceId, Account = GlAccount.ExternalClearing,
            Debit = reverse ? 0 : amount, Credit = reverse ? amount : 0, Currency = currency, Memo = memo, CreatedAt = now
        });
        _context.GlEntries.Add(new GlEntry
        {
            Id = Guid.NewGuid(), TransactionId = referenceId, Account = GlAccount.Cash,
            Debit = reverse ? amount : 0, Credit = reverse ? 0 : amount, Currency = currency, Memo = memo, CreatedAt = now
        });
        await _context.SaveChangesAsync();
    }

    private static GlAccount DefaultCounter(TransactionType type) => type switch
    {
        TransactionType.Deposit or TransactionType.Withdrawal => GlAccount.Cash,
        TransactionType.Transfer => GlAccount.TransferClearing,
        TransactionType.ExternalTransfer => GlAccount.ExternalClearing,
        TransactionType.CardTopUp => GlAccount.ProcessorReceivable,
        TransactionType.Payment or TransactionType.Refund => GlAccount.PaymentsClearing,
        TransactionType.Interest => GlAccount.InterestExpense,
        TransactionType.Fee => GlAccount.FeeIncome,
        _ => GlAccount.Cash
    };

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

    /// <summary>Compares every account's stored balance with its ledger-derived balance in two queries.</summary>
    public async Task<ReconciliationReport> ReconcileAllAsync()
    {
        var ledgerTotals = await _context.LedgerEntries
            .GroupBy(l => l.AccountId)
            .Select(g => new { AccountId = g.Key, Balance = g.Sum(l => l.Credit - l.Debit) })
            .ToDictionaryAsync(x => x.AccountId, x => x.Balance);

        var accounts = await _context.Accounts.IgnoreQueryFilters().AsNoTracking()
            .Select(a => new { a.Id, a.AccountNumber, a.Balance })
            .ToListAsync();

        var report = new ReconciliationReport { AccountsChecked = accounts.Count };
        foreach (var account in accounts)
        {
            var ledger = ledgerTotals.GetValueOrDefault(account.Id);
            if (Math.Abs(account.Balance - ledger) >= 0.01m)
            {
                report.Mismatches.Add(new ReconciliationMismatch
                {
                    AccountId = account.Id,
                    AccountNumber = account.AccountNumber,
                    StoredBalance = account.Balance,
                    LedgerBalance = ledger
                });
            }
        }

        return report;
    }

    public async Task<TrialBalance> GetTrialBalanceAsync(DateTime? asOf = null)
    {
        var cutoff = asOf ?? DateTime.UtcNow;

        var rows = await _context.GlEntries.AsNoTracking()
            .Where(e => e.CreatedAt <= cutoff)
            .GroupBy(e => new { e.Account, e.Currency })
            .Select(g => new { g.Key.Account, g.Key.Currency, Debits = g.Sum(e => e.Debit), Credits = g.Sum(e => e.Credit) })
            .ToListAsync();

        var result = new TrialBalance { AsOf = cutoff };
        result.Lines = rows
            .OrderBy(r => r.Currency).ThenBy(r => r.Account)
            .Select(r => new TrialBalanceLine
            {
                Account = r.Account.ToString(),
                Currency = r.Currency,
                Debits = r.Debits,
                Credits = r.Credits,
                Net = r.Debits - r.Credits
            })
            .ToList();

        result.Totals = result.Lines
            .GroupBy(l => l.Currency)
            .Select(g => new TrialBalanceTotal
            {
                Currency = g.Key,
                Debits = g.Sum(l => l.Debits),
                Credits = g.Sum(l => l.Credits),
                Balanced = g.Sum(l => l.Debits) == g.Sum(l => l.Credits)
            })
            .ToList();

        return result;
    }

    public async Task<BalanceSheet> GetBalanceSheetAsync(DateTime? asOf = null)
    {
        var trial = await GetTrialBalanceAsync(asOf);
        var sheet = new BalanceSheet { AsOf = trial.AsOf };

        foreach (var group in trial.Lines.GroupBy(l => l.Currency))
        {
            decimal Net(GlAccount account) =>
                group.FirstOrDefault(l => l.Account == account.ToString())?.Net ?? 0;

            var section = new BalanceSheetSection { Currency = group.Key };

            // Debit-normal accounts are shown as debits minus credits; credit-normal as credits minus debits.
            section.Assets["Cash"] = Net(GlAccount.Cash);
            section.Assets["ProcessorReceivable"] = Net(GlAccount.ProcessorReceivable);
            section.Liabilities["CustomerDeposits"] = -Net(GlAccount.CustomerDeposits);
            section.Liabilities["TransferClearing"] = -Net(GlAccount.TransferClearing);
            section.Liabilities["PaymentsClearing"] = -Net(GlAccount.PaymentsClearing);
            section.Liabilities["ExternalClearing"] = -Net(GlAccount.ExternalClearing);
            section.RetainedEarnings = -Net(GlAccount.FeeIncome) - Net(GlAccount.InterestExpense);

            section.TotalAssets = section.Assets.Values.Sum();
            section.TotalLiabilities = section.Liabilities.Values.Sum();
            section.Balanced = section.TotalAssets == section.TotalLiabilities + section.RetainedEarnings;

            sheet.Sections.Add(section);
        }

        return sheet;
    }

    /// <summary>
    /// Closes an accounting period: snapshots every account's totals and closing balance and records the
    /// GL totals. The period must be wholly in the past and must not overlap another period.
    /// </summary>
    public async Task<LedgerPeriod> ClosePeriodAsync(string name, DateTime start, DateTime end, Guid closedByUserId)
    {
        start = DateTime.SpecifyKind(start.AsUtc().Date, DateTimeKind.Utc);
        end = DateTime.SpecifyKind(end.AsUtc().Date, DateTimeKind.Utc);

        if (end < start) throw new InvalidOperationException("The period end must not be before its start.");
        if (end >= DateTime.UtcNow.Date) throw new InvalidOperationException("Only periods that have fully ended can be closed.");

        var overlaps = await _context.LedgerPeriods.AnyAsync(p => p.StartDate <= end && p.EndDate >= start);
        if (overlaps) throw new InvalidOperationException("The period overlaps an existing accounting period.");

        var rangeEnd = end.AddDays(1);

        var movement = await _context.LedgerEntries.AsNoTracking()
            .Where(l => l.CreatedAt >= start && l.CreatedAt < rangeEnd)
            .GroupBy(l => l.AccountId)
            .Select(g => new { AccountId = g.Key, Debits = g.Sum(l => l.Debit), Credits = g.Sum(l => l.Credit) })
            .ToListAsync();

        var closing = await _context.LedgerEntries.AsNoTracking()
            .Where(l => l.CreatedAt < rangeEnd)
            .GroupBy(l => l.AccountId)
            .Select(g => new { AccountId = g.Key, Balance = g.Sum(l => l.Credit - l.Debit) })
            .ToDictionaryAsync(x => x.AccountId, x => x.Balance);

        var currencies = await _context.Accounts.IgnoreQueryFilters().AsNoTracking()
            .Select(a => new { a.Id, a.Currency })
            .ToDictionaryAsync(a => a.Id, a => a.Currency);

        var gl = await _context.GlEntries.AsNoTracking()
            .Where(e => e.CreatedAt >= start && e.CreatedAt < rangeEnd)
            .GroupBy(_ => 1)
            .Select(g => new { Debits = g.Sum(e => e.Debit), Credits = g.Sum(e => e.Credit) })
            .FirstOrDefaultAsync();

        var period = new LedgerPeriod
        {
            Id = Guid.NewGuid(),
            Name = name.Trim(),
            StartDate = start,
            EndDate = end,
            Status = LedgerPeriodStatus.Closed,
            TotalDebits = gl?.Debits ?? 0,
            TotalCredits = gl?.Credits ?? 0,
            ClosedByUserId = closedByUserId,
            ClosedAt = DateTime.UtcNow
        };

        foreach (var row in movement)
        {
            period.Balances.Add(new PeriodBalance
            {
                Id = Guid.NewGuid(),
                AccountId = row.AccountId,
                Currency = currencies.GetValueOrDefault(row.AccountId, "USD"),
                Debits = row.Debits,
                Credits = row.Credits,
                ClosingBalance = closing.GetValueOrDefault(row.AccountId)
            });
        }

        _context.LedgerPeriods.Add(period);
        await _context.SaveChangesAsync();
        return period;
    }

    public async Task<List<LedgerPeriod>> GetPeriodsAsync() =>
        await _context.LedgerPeriods.AsNoTracking().OrderByDescending(p => p.StartDate).ToListAsync();

    /// <summary>Entries dated inside a closed period are not allowed, so closed books cannot change.</summary>
    private async Task EnsurePeriodOpenAsync(DateTime when)
    {
        var date = when.Date;
        var closed = await _context.LedgerPeriods.AsNoTracking()
            .FirstOrDefaultAsync(p => p.Status == LedgerPeriodStatus.Closed && p.StartDate <= date && p.EndDate >= date);

        if (closed != null)
        {
            throw new InvalidOperationException($"The accounting period '{closed.Name}' is closed.");
        }
    }
}
