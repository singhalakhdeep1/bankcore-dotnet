using System.Data;
using System.Text.Json;
using BankCore.Services.Caching;
using BankingAPI.Data;
using BankingAPI.Models;
using BankingAPI.Services.Caching;
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

    /// <summary>Credits the account for money captured from a card by the payment gateway.</summary>
    Task<Transaction?> ProcessCardTopUpAsync(Guid accountId, decimal amount, string description, string referenceNumber);

    /// <summary>Debits the account for a fee; the income is booked to the fee-income GL account.</summary>
    Task<Transaction?> ProcessFeeAsync(Guid accountId, decimal amount, string description, string? referenceNumber = null);

    /// <summary>Debits the account for an outgoing ACH/wire/SWIFT transfer; the in-flight amount is booked to external clearing.</summary>
    Task<Transaction?> ProcessExternalDebitAsync(Guid accountId, decimal amount, string description, string referenceNumber, string? beneficiaryName = null);

    Task<Transaction?> ReverseTransactionAsync(Guid transactionId, string reason);
    Task<bool> ValidateTransactionAsync(Guid accountId, decimal amount, TransactionType type);
    Task<List<Transaction>> GetPendingTransactionsAsync();
    Task<List<Transaction>> GetSuspiciousTransactionsAsync();
}

/// <summary>A business rule or compliance check refused the transaction. The message is safe to show to the customer.</summary>
public class TransactionRejectedException : InvalidOperationException
{
    public string Code { get; }

    public TransactionRejectedException(string code, string message) : base(message)
    {
        Code = code;
    }
}

/// <summary>
/// Moves money. Conventions used throughout:
/// - <see cref="Transaction.Amount"/> is signed: credits are positive, debits negative.
/// - Every posting (transaction row + balance change + ledger and GL entries) commits in one database
///   transaction, so a failure never leaves a half-applied transfer.
/// - Account rows carry a concurrency token, so two requests racing on the same balance cannot both win.
/// - Outgoing money passes the compliance gate first (KYC status and limits, sanctions, risk hold).
/// </summary>
public class TransactionService : ITransactionService
{
    /// <summary>Fallback daily limit when no KYC-based limit is available.</summary>
    public const decimal DailyDebitLimit = 10_000m;
    private const decimal MaxAmount = 1_000_000_000m;

    private readonly ApplicationDbContext _context;
    private readonly ILedgerService _ledgerService;
    private readonly IComplianceService _complianceService;
    private readonly ILogger<TransactionService>? _logger;
    private readonly IHttpContextAccessor? _http;
    private readonly DistributedCacheService? _cache;
    private readonly IAccountCache? _accountCache;
    private readonly ReadDbContext? _readContext;

    // Accounts touched by the current unit of work, whose cached views are invalidated after commit.
    private readonly HashSet<(Guid AccountId, Guid UserId)> _touched = new();

    public TransactionService(
        ApplicationDbContext context,
        ILedgerService ledgerService,
        IComplianceService complianceService,
        ILogger<TransactionService>? logger = null,
        IHttpContextAccessor? http = null,
        DistributedCacheService? cache = null,
        IAccountCache? accountCache = null,
        ReadDbContext? readContext = null)
    {
        _context = context;
        _ledgerService = ledgerService;
        _complianceService = complianceService;
        _logger = logger;
        _http = http;
        _cache = cache;
        _accountCache = accountCache;
        _readContext = readContext;
    }

    // ------------------------------------------------------------------ reads

    public async Task<List<Transaction>> GetAccountTransactionsAsync(Guid accountId)
    {
        var db = _readContext ?? _context;
        return await db.Transactions.AsNoTracking()
            .Where(t => t.AccountId == accountId)
            .OrderByDescending(t => t.CreatedAt)
            .ToListAsync();
    }

    public async Task<(List<Transaction> transactions, int totalCount)> GetAccountTransactionsAsync(Guid accountId, int page, int pageSize)
    {
        page = Math.Max(1, page);
        pageSize = Math.Clamp(pageSize, 1, 100);

        async Task<CachedPage> Load()
        {
            // History is read through the read-replica context when one is available.
            var db = _readContext ?? _context;
            var query = db.Transactions.AsNoTracking().Where(t => t.AccountId == accountId);
            var total = await query.CountAsync();
            var items = await query
                .OrderByDescending(t => t.CreatedAt)
                .ThenByDescending(t => t.Id)
                .Skip((page - 1) * pageSize)
                .Take(pageSize)
                .ToListAsync();
            return new CachedPage { Items = items, TotalCount = total };
        }

        CachedPage result;
        if (_cache != null && _accountCache != null)
        {
            var version = await _accountCache.AccountVersionAsync(accountId);
            result = await _cache.GetOrSetAsync($"tx:{accountId}:{version}:{page}:{pageSize}", Load, TimeSpan.FromSeconds(60));
        }
        else
        {
            result = await Load();
        }

        return (result.Items, result.TotalCount);
    }

    public class CachedPage
    {
        public List<Transaction> Items { get; set; } = new();
        public int TotalCount { get; set; }
    }

    // ------------------------------------------------------------------ simple operations

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
            "Withdrawal failed. The account must be active, the amount valid, funds sufficient and the daily limit not exceeded.");
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

        await RequireComplianceAsync(accountId, amount, TransactionType.Deposit);

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

        await RequireComplianceAsync(accountId, amount, TransactionType.Withdrawal);
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

        await RequireComplianceAsync(accountId, amount, TransactionType.Payment, merchantName);
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

    public async Task<Transaction?> ProcessCardTopUpAsync(Guid accountId, decimal amount, string description, string referenceNumber)
    {
        if (!IsValidAmount(amount)) return null;

        var account = await _context.Accounts.FindAsync(accountId);
        if (account == null || account.Status != AccountStatus.Active) return null;

        await RequireComplianceAsync(accountId, amount, TransactionType.Deposit);

        var transaction = await InTransactionAsync(() =>
            PostAsync(account, TransactionType.CardTopUp, amount, description, referenceNumber, glCounter: GlAccount.ProcessorReceivable));

        await RunComplianceAsync(transaction);
        return transaction;
    }

    public async Task<Transaction?> ProcessFeeAsync(Guid accountId, decimal amount, string description, string? referenceNumber = null)
    {
        if (!IsValidAmount(amount)) return null;

        var account = await _context.Accounts.FindAsync(accountId);
        if (account == null || account.Status == AccountStatus.Closed) return null;

        return await InTransactionAsync(() =>
            PostAsync(account, TransactionType.Fee, -amount, description, referenceNumber ?? NewReference("FEE"), glCounter: GlAccount.FeeIncome));
    }

    public async Task<Transaction?> ProcessExternalDebitAsync(
        Guid accountId, decimal amount, string description, string referenceNumber, string? beneficiaryName = null)
    {
        if (!IsValidAmount(amount)) return null;

        var account = await _context.Accounts.FindAsync(accountId);
        if (account == null || account.Status != AccountStatus.Active) return null;
        if (!CanDebit(account, amount)) return null;

        await RequireComplianceAsync(accountId, amount, TransactionType.ExternalTransfer, beneficiaryName);
        if (await ExceedsDailyLimitAsync(accountId, amount)) return null;

        var transaction = await InTransactionAsync(() =>
            PostAsync(account, TransactionType.ExternalTransfer, -amount, description, referenceNumber, glCounter: GlAccount.ExternalClearing));

        await RunComplianceAsync(transaction);
        return transaction;
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

                var reversal = await PostAsync(
                    account, TransactionType.Reversal, -leg.Amount, $"Reversal: {reason}", reversalReference,
                    glCounter: leg.GlCounter ?? DefaultCounterFor(leg.Type), reverses: leg.Id);
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

        var isDebit = type is TransactionType.Withdrawal or TransactionType.Payment or TransactionType.Transfer or TransactionType.ExternalTransfer;
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

        var toAccount = await _context.Accounts.Include(a => a.User).FirstOrDefaultAsync(a => a.AccountNumber == toAccountNumber);
        if (toAccount == null || toAccount.Status != AccountStatus.Active || toAccount.Id == fromAccount.Id) return (null, null);

        if (!string.Equals(fromAccount.Currency, toAccount.Currency, StringComparison.OrdinalIgnoreCase)) return (null, null);
        if (!CanDebit(fromAccount, amount)) return (null, null);

        await RequireComplianceAsync(
            fromAccountId, amount, TransactionType.Transfer,
            $"{toAccount.User.FirstName} {toAccount.User.LastName}", toAccount.AccountNumber);

        // The recipient must also be allowed to receive (rejected KYC cannot receive funds).
        var receiving = await _complianceService.EvaluateAsync(toAccount.Id, amount, TransactionType.Deposit);
        if (!receiving.Allowed)
        {
            throw new TransactionRejectedException(receiving.Code ?? "RECIPIENT", "The recipient account cannot receive funds right now.");
        }

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
    /// Applies a signed amount to an account: updates the balance, writes the transaction row,
    /// the customer ledger entry and the balancing GL entries. Must run inside <see cref="InTransactionAsync{T}"/>.
    /// </summary>
    private async Task<Transaction> PostAsync(
        Account account,
        TransactionType type,
        decimal signedAmount,
        string description,
        string referenceNumber,
        string? toAccountNumber = null,
        string? fromAccountNumber = null,
        GlAccount? glCounter = null,
        Guid? reverses = null)
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
            IpAddress = _http?.HttpContext?.Connection.RemoteIpAddress?.ToString(),
            GlCounter = glCounter ?? DefaultCounterFor(type),
            ReversesTransactionId = reverses,
            CreatedAt = now,
            UpdatedAt = now
        };

        account.Balance += signedAmount;
        account.UpdatedAt = now;
        transaction.BalanceAfter = account.Balance;

        _context.Transactions.Add(transaction);
        await _context.SaveChangesAsync();
        await _ledgerService.RecordTransactionAsync(transaction);

        _touched.Add((account.Id, account.UserId));
        return transaction;
    }

    private static GlAccount DefaultCounterFor(TransactionType type) => type switch
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

    /// <summary>Runs <paramref name="work"/> in a database transaction (a no-op for non-relational test providers).</summary>
    private async Task<T> InTransactionAsync<T>(Func<Task<T>> work)
    {
        T result;

        if (!_context.Database.IsRelational() || _context.Database.CurrentTransaction != null)
        {
            result = await work();
        }
        else
        {
            await using var dbTransaction = await _context.Database.BeginTransactionAsync(IsolationLevel.ReadCommitted);
            try
            {
                result = await work();
                await dbTransaction.CommitAsync();
            }
            catch
            {
                await dbTransaction.RollbackAsync();
                _context.ChangeTracker.Clear();
                _touched.Clear();
                throw;
            }
        }

        await InvalidateCachesAsync();
        return result;
    }

    private async Task InvalidateCachesAsync()
    {
        if (_accountCache == null || _touched.Count == 0)
        {
            _touched.Clear();
            return;
        }

        foreach (var (accountId, userId) in _touched)
        {
            await _accountCache.BumpAsync(accountId, userId);
        }

        _touched.Clear();
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
                "the amount valid and the daily limit not exceeded.");
        }

        return result;
    }

    /// <summary>Applies the pre-transaction compliance gate; throws <see cref="TransactionRejectedException"/> when refused.</summary>
    private async Task RequireComplianceAsync(
        Guid accountId, decimal amount, TransactionType type, string? counterpartyName = null, string? counterpartyAccount = null)
    {
        var decision = await _complianceService.EvaluateAsync(accountId, amount, type, counterpartyName, counterpartyAccount);

        // A null decision can only come from a stubbed service; treat it as "no objection".
        if (decision is { Allowed: false })
        {
            throw new TransactionRejectedException(decision.Code ?? "COMPLIANCE", decision.Reason ?? "The transaction was refused by compliance.");
        }
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
        var limit = await _complianceService.GetDailyDebitLimitAsync(accountId);
        if (limit <= 0) limit = DailyDebitLimit;

        var today = DateTime.UtcNow.Date;
        var dailyTotal = await _context.Transactions
            .Where(t => t.AccountId == accountId
                        && t.CreatedAt >= today
                        && t.Status == TransactionStatus.Completed
                        && t.Amount < 0
                        && (t.Type == TransactionType.Withdrawal
                            || t.Type == TransactionType.Payment
                            || t.Type == TransactionType.Transfer
                            || t.Type == TransactionType.ExternalTransfer))
            .SumAsync(t => -t.Amount);

        return dailyTotal + amount > limit;
    }

    private static bool CanDebit(Account account, decimal amount) => account.Balance + account.OverdraftLimit >= amount;

    private static bool IsValidAmount(decimal amount) =>
        amount > 0 && amount <= MaxAmount && decimal.Round(amount, 2) == amount;

    private static string NewReference(string prefix) =>
        $"{prefix}{DateTime.UtcNow:yyyyMMddHHmmss}{Guid.NewGuid().ToString("N")[..8].ToUpperInvariant()}";
}
