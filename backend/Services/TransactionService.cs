using System.Data;
using BankingAPI.Data;
using BankingAPI.Models;
using Microsoft.EntityFrameworkCore;

namespace BankingAPI.Services;

public interface ITransactionService
{
    Task<List<Transaction>> GetAccountTransactionsAsync(Guid accountId);
    Task<(List<Transaction> transactions, int totalCount)> GetAccountTransactionsAsync(Guid accountId, int page, int pageSize);
    Task<Transaction?> ProcessDepositAsync(Guid accountId, decimal amount, string? description = null);
    Task<Transaction?> DepositAsync(Guid accountId, decimal amount, string? description = null);
    Task<Transaction?> ProcessWithdrawalAsync(Guid accountId, decimal amount, string? description = null);
    Task<Transaction?> WithdrawAsync(Guid accountId, decimal amount, string? description = null);
    Task<(Transaction? fromTx, Transaction? toTx)> ProcessTransferAsync(Guid fromAccountId, string toAccountNumber, decimal amount, string? description = null);
    Task<(Transaction? fromTx, Transaction? toTx)> TransferAsync(Guid fromAccountId, Guid toAccountId, decimal amount, string? description = null, string? idempotencyKey = null);
    Task<Transaction?> ProcessPaymentAsync(Guid accountId, decimal amount, string merchantName, string? description = null);
    Task<Transaction?> ProcessRefundAsync(Guid accountId, decimal amount, string reason);
    Task<Transaction?> ReverseTransactionAsync(Guid transactionId, string reason);
    Task<bool> ValidateTransactionAsync(Guid accountId, decimal amount, TransactionType type);
    Task<List<Transaction>> GetPendingTransactionsAsync();
    Task<List<Transaction>> GetSuspiciousTransactionsAsync();
}

/// <summary>
/// Moves money. Conventions used throughout:
/// - <see cref="Transaction.Amount"/> is signed: credits are positive, debits negative.
/// - Every posting (transaction row + balance change + ledger entry) happens inside one database
///   transaction, so a failure never leaves a half-applied transfer.
/// - Account rows carry a concurrency token, so two requests racing on the same balance cannot both win.
/// </summary>
public class TransactionService : ITransactionService
{
    public const decimal DailyDebitLimit = 10_000m;
    private const decimal MaxAmount = 1_000_000_000m;

    private readonly ApplicationDbContext _context;
    private readonly ILedgerService _ledgerService;
    private readonly IComplianceService _complianceService;
    private readonly ILogger<TransactionService>? _logger;

    public TransactionService(
        ApplicationDbContext context,
        ILedgerService ledgerService,
        IComplianceService complianceService,
        ILogger<TransactionService>? logger = null)
    {
        _context = context;
        _ledgerService = ledgerService;
        _complianceService = complianceService;
        _logger = logger;
    }

    public async Task<List<Transaction>> GetAccountTransactionsAsync(Guid accountId)
    {
        return await _context.Transactions
            .Where(t => t.AccountId == accountId)
            .OrderByDescending(t => t.CreatedAt)
            .ToListAsync();
    }

    public async Task<(List<Transaction> transactions, int totalCount)> GetAccountTransactionsAsync(Guid accountId, int page, int pageSize)
    {
        page = Math.Max(1, page);
        pageSize = Math.Clamp(pageSize, 1, 100);

        var query = _context.Transactions.Where(t => t.AccountId == accountId);
        var totalCount = await query.CountAsync();

        var transactions = await query
            .OrderByDescending(t => t.CreatedAt)
            .ThenByDescending(t => t.Id)
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .ToListAsync();

        return (transactions, totalCount);
    }

    public async Task<Transaction?> DepositAsync(Guid accountId, decimal amount, string? description = null)
    {
        var transaction = await ProcessDepositAsync(accountId, amount, description);
        return transaction ?? throw new InvalidOperationException(
            "Deposit failed. Check that the account is active and the amount is valid (positive, at most 2 decimals).");
    }

    public async Task<Transaction?> WithdrawAsync(Guid accountId, decimal amount, string? description = null)
    {
        var transaction = await ProcessWithdrawalAsync(accountId, amount, description);
        return transaction ?? throw new InvalidOperationException(
            $"Withdrawal failed. The account must be active, the amount valid, funds sufficient and the {DailyDebitLimit:N0} daily limit not exceeded.");
    }

    public async Task<(Transaction? fromTx, Transaction? toTx)> TransferAsync(
        Guid fromAccountId, Guid toAccountId, decimal amount, string? description = null, string? idempotencyKey = null)
    {
        if (fromAccountId == toAccountId)
        {
            throw new InvalidOperationException("Cannot transfer to the same account.");
        }

        var toAccount = await _context.Accounts.AsNoTracking().FirstOrDefaultAsync(a => a.Id == toAccountId)
            ?? throw new InvalidOperationException("Destination account not found.");

        if (string.IsNullOrWhiteSpace(idempotencyKey))
        {
            var result = await TransferCoreAsync(fromAccountId, toAccount.AccountNumber, amount, description, NewReference("TXN"));
            return RequireTransfer(result);
        }

        // Replay: the same key returns the original transfer instead of moving money twice.
        var existing = await FindTransferLegsAsync(fromAccountId, toAccountId, idempotencyKey);
        if (existing.fromTx != null) return existing;

        try
        {
            var result = await TransferCoreAsync(fromAccountId, toAccount.AccountNumber, amount, description, idempotencyKey);
            return RequireTransfer(result);
        }
        catch (DbUpdateException)
        {
            // A concurrent request with the same key won the unique index; return its result.
            _context.ChangeTracker.Clear();
            existing = await FindTransferLegsAsync(fromAccountId, toAccountId, idempotencyKey);
            if (existing.fromTx != null) return existing;
            throw;
        }
    }

    public async Task<Transaction?> ProcessDepositAsync(Guid accountId, decimal amount, string? description = null)
    {
        if (!IsValidAmount(amount)) return null;

        var account = await _context.Accounts.FindAsync(accountId);
        if (account == null || account.Status != AccountStatus.Active) return null;

        var transaction = await InTransactionAsync(() =>
            PostAsync(account, TransactionType.Deposit, amount, description ?? "Deposit", NewReference("TXN")));

        await RunComplianceAsync(transaction);
        return transaction;
    }

    public async Task<Transaction?> ProcessWithdrawalAsync(Guid accountId, decimal amount, string? description = null)
    {
        if (!IsValidAmount(amount)) return null;

        var account = await _context.Accounts.FindAsync(accountId);
        if (account == null || account.Status != AccountStatus.Active) return null;
        if (!CanDebit(account, amount)) return null;
        if (await ExceedsDailyLimitAsync(accountId, amount)) return null;

        var transaction = await InTransactionAsync(() =>
            PostAsync(account, TransactionType.Withdrawal, -amount, description ?? "Withdrawal", NewReference("TXN")));

        await RunComplianceAsync(transaction);
        return transaction;
    }

    public async Task<(Transaction? fromTx, Transaction? toTx)> ProcessTransferAsync(
        Guid fromAccountId,
        string toAccountNumber,
        decimal amount,
        string? description = null)
    {
        return await TransferCoreAsync(fromAccountId, toAccountNumber, amount, description, NewReference("TXN"));
    }

    public async Task<Transaction?> ProcessPaymentAsync(Guid accountId, decimal amount, string merchantName, string? description = null)
    {
        if (!IsValidAmount(amount)) return null;

        var account = await _context.Accounts.FindAsync(accountId);
        if (account == null || account.Status != AccountStatus.Active) return null;
        if (!CanDebit(account, amount)) return null;
        if (await ExceedsDailyLimitAsync(accountId, amount)) return null;

        var transaction = await InTransactionAsync(() =>
            PostAsync(account, TransactionType.Payment, -amount, description ?? $"Payment to {merchantName}", NewReference("TXN")));

        await RunComplianceAsync(transaction);
        return transaction;
    }

    public async Task<Transaction?> ProcessRefundAsync(Guid accountId, decimal amount, string reason)
    {
        if (!IsValidAmount(amount)) return null;

        var account = await _context.Accounts.FindAsync(accountId);
        if (account == null || account.Status == AccountStatus.Closed) return null;

        return await InTransactionAsync(() =>
            PostAsync(account, TransactionType.Refund, amount, $"Refund: {reason}", NewReference("REF")));
    }

    /// <summary>
    /// Reverses a completed transaction. Reversing one leg of a transfer reverses both legs.
    /// Returns the reversal posted to the original transaction's account, or null if it cannot be reversed.
    /// </summary>
    public async Task<Transaction?> ReverseTransactionAsync(Guid transactionId, string reason)
    {
        var original = await _context.Transactions.FindAsync(transactionId);
        if (original == null || original.Status != TransactionStatus.Completed || original.Type == TransactionType.Reversal)
        {
            return null;
        }

        var legs = original.Type == TransactionType.Transfer
            ? await _context.Transactions
                .Where(t => t.ReferenceNumber == original.ReferenceNumber
                            && t.Type == TransactionType.Transfer
                            && t.Status == TransactionStatus.Completed)
                .ToListAsync()
            : new List<Transaction> { original };

        return await InTransactionAsync(async () =>
        {
            Transaction? result = null;
            var reversalReference = NewReference("REV");

            foreach (var leg in legs)
            {
                var account = await _context.Accounts.FindAsync(leg.AccountId)
                    ?? throw new InvalidOperationException("Account for the transaction no longer exists.");

                // Reversing a credit debits the account, which must not break its overdraft limit.
                if (leg.Amount > 0 && !CanDebit(account, leg.Amount))
                {
                    throw new InvalidOperationException("Insufficient funds to reverse this transaction.");
                }

                var reversal = await PostAsync(account, TransactionType.Reversal, -leg.Amount, $"Reversal: {reason}", reversalReference);
                leg.Status = TransactionStatus.Reversed;
                leg.UpdatedAt = DateTime.UtcNow;

                if (leg.Id == original.Id) result = reversal;
            }

            await _context.SaveChangesAsync();
            return result;
        });
    }

    public async Task<bool> ValidateTransactionAsync(Guid accountId, decimal amount, TransactionType type)
    {
        if (!IsValidAmount(amount)) return false;

        var account = await _context.Accounts.FindAsync(accountId);
        if (account == null || account.Status != AccountStatus.Active) return false;

        var isDebit = type is TransactionType.Withdrawal or TransactionType.Payment or TransactionType.Transfer;
        if (!isDebit) return true;

        if (!CanDebit(account, amount)) return false;
        return !await ExceedsDailyLimitAsync(accountId, amount);
    }

    public async Task<List<Transaction>> GetPendingTransactionsAsync()
    {
        return await _context.Transactions
            .Where(t => t.Status == TransactionStatus.Pending)
            .OrderBy(t => t.CreatedAt)
            .ToListAsync();
    }

    public async Task<List<Transaction>> GetSuspiciousTransactionsAsync()
    {
        return await _context.Transactions
            .Where(t => t.IsFraudulent || t.Amount > 5000 || t.Amount < -5000)
            .OrderByDescending(t => t.CreatedAt)
            .ToListAsync();
    }

    // ------------------------------------------------------------------ internals

    private async Task<(Transaction? fromTx, Transaction? toTx)> TransferCoreAsync(
        Guid fromAccountId, string toAccountNumber, decimal amount, string? description, string referenceNumber)
    {
        if (!IsValidAmount(amount)) return (null, null);

        var fromAccount = await _context.Accounts.FindAsync(fromAccountId);
        if (fromAccount == null || fromAccount.Status != AccountStatus.Active) return (null, null);

        var toAccount = await _context.Accounts.FirstOrDefaultAsync(a => a.AccountNumber == toAccountNumber);
        if (toAccount == null || toAccount.Status != AccountStatus.Active || toAccount.Id == fromAccount.Id) return (null, null);

        if (!string.Equals(fromAccount.Currency, toAccount.Currency, StringComparison.OrdinalIgnoreCase)) return (null, null);
        if (!CanDebit(fromAccount, amount)) return (null, null);
        if (await ExceedsDailyLimitAsync(fromAccountId, amount)) return (null, null);

        var (fromTx, toTx) = await InTransactionAsync(async () =>
        {
            var debit = await PostAsync(
                fromAccount, TransactionType.Transfer, -amount,
                description ?? $"Transfer to {toAccount.AccountNumber}", referenceNumber,
                toAccountNumber: toAccount.AccountNumber);

            var credit = await PostAsync(
                toAccount, TransactionType.Transfer, amount,
                description ?? $"Transfer from {fromAccount.AccountNumber}", referenceNumber,
                fromAccountNumber: fromAccount.AccountNumber);

            return (debit, credit);
        });

        await RunComplianceAsync(fromTx);
        return (fromTx, toTx);
    }

    /// <summary>
    /// Applies a signed amount to an account: updates the balance, writes the transaction row
    /// and the matching ledger entry. Must run inside <see cref="InTransactionAsync{T}"/>.
    /// </summary>
    private async Task<Transaction> PostAsync(
        Account account,
        TransactionType type,
        decimal signedAmount,
        string description,
        string referenceNumber,
        string? toAccountNumber = null,
        string? fromAccountNumber = null)
    {
        var now = DateTime.UtcNow;
        var transaction = new Transaction
        {
            Id = Guid.NewGuid(),
            AccountId = account.Id,
            Type = type,
            Amount = signedAmount,
            Currency = account.Currency,
            BalanceBefore = account.Balance,
            Description = description,
            ReferenceNumber = referenceNumber,
            ToAccountNumber = toAccountNumber,
            FromAccountNumber = fromAccountNumber,
            Status = TransactionStatus.Completed,
            CreatedAt = now,
            UpdatedAt = now
        };

        account.Balance += signedAmount;
        account.UpdatedAt = now;
        transaction.BalanceAfter = account.Balance;

        _context.Transactions.Add(transaction);
        await _context.SaveChangesAsync();
        await _ledgerService.RecordTransactionAsync(transaction);

        return transaction;
    }

    /// <summary>Runs <paramref name="work"/> in a database transaction (a no-op for non-relational test providers).</summary>
    private async Task<T> InTransactionAsync<T>(Func<Task<T>> work)
    {
        if (!_context.Database.IsRelational() || _context.Database.CurrentTransaction != null)
        {
            return await work();
        }

        await using var dbTransaction = await _context.Database.BeginTransactionAsync(IsolationLevel.ReadCommitted);
        try
        {
            var result = await work();
            await dbTransaction.CommitAsync();
            return result;
        }
        catch
        {
            await dbTransaction.RollbackAsync();
            _context.ChangeTracker.Clear();
            throw;
        }
    }

    private async Task<(Transaction? fromTx, Transaction? toTx)> FindTransferLegsAsync(
        Guid fromAccountId, Guid toAccountId, string referenceNumber)
    {
        var fromTx = await _context.Transactions.AsNoTracking()
            .FirstOrDefaultAsync(t => t.AccountId == fromAccountId && t.ReferenceNumber == referenceNumber);
        if (fromTx == null) return (null, null);

        var toTx = await _context.Transactions.AsNoTracking()
            .FirstOrDefaultAsync(t => t.AccountId == toAccountId && t.ReferenceNumber == referenceNumber);
        return (fromTx, toTx);
    }

    private static (Transaction? fromTx, Transaction? toTx) RequireTransfer((Transaction? fromTx, Transaction? toTx) result)
    {
        if (result.fromTx == null || result.toTx == null)
        {
            throw new InvalidOperationException(
                "Transfer failed. Both accounts must be active and in the same currency, funds sufficient, " +
                $"the amount valid and the {DailyDebitLimit:N0} daily limit not exceeded.");
        }

        return result;
    }

    private async Task RunComplianceAsync(Transaction transaction)
    {
        // Monitoring must never undo or fail a payment that has already been committed.
        try
        {
            await _complianceService.CheckTransactionAsync(transaction);
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "Compliance check failed for transaction {TransactionId}", transaction.Id);
        }
    }

    private async Task<bool> ExceedsDailyLimitAsync(Guid accountId, decimal amount)
    {
        var today = DateTime.UtcNow.Date;
        var dailyTotal = await _context.Transactions
            .Where(t => t.AccountId == accountId
                        && t.CreatedAt >= today
                        && t.Status == TransactionStatus.Completed
                        && t.Amount < 0
                        && (t.Type == TransactionType.Withdrawal
                            || t.Type == TransactionType.Payment
                            || t.Type == TransactionType.Transfer))
            .SumAsync(t => -t.Amount);

        return dailyTotal + amount > DailyDebitLimit;
    }

    private static bool CanDebit(Account account, decimal amount) => account.Balance + account.OverdraftLimit >= amount;

    private static bool IsValidAmount(decimal amount) =>
        amount > 0 && amount <= MaxAmount && decimal.Round(amount, 2) == amount;

    private static string NewReference(string prefix) =>
        $"{prefix}{DateTime.UtcNow:yyyyMMddHHmmss}{Guid.NewGuid().ToString("N")[..8].ToUpperInvariant()}";
}
