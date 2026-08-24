using BankingAPI.Data;
using BankingAPI.Models;
using BankingAPI.DTOs;
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
    Task<Transaction?> ReverseTransactionAsync(Guid transactionId, string reason);
    Task<bool> ValidateTransactionAsync(Guid accountId, decimal amount, TransactionType type);
    Task<List<Transaction>> GetPendingTransactionsAsync();
    Task<List<Transaction>> GetSuspiciousTransactionsAsync();
}

public class TransactionService : ITransactionService
{
    private readonly ApplicationDbContext _context;
    private readonly ILedgerService _ledgerService;
    private readonly IComplianceService _complianceService;

    public TransactionService(
        ApplicationDbContext context,
        ILedgerService ledgerService,
        IComplianceService complianceService)
    {
        _context = context;
        _ledgerService = ledgerService;
        _complianceService = complianceService;
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
        var query = _context.Transactions
            .Where(t => t.AccountId == accountId);
        
        var totalCount = await query.CountAsync();
        
        var transactions = await query
            .OrderByDescending(t => t.CreatedAt)
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .ToListAsync();

        return (transactions, totalCount);
    }

    public async Task<Transaction?> DepositAsync(Guid accountId, decimal amount, string? description = null)
    {
        var transaction = await ProcessDepositAsync(accountId, amount, description);
        if (transaction == null)
        {
            throw new InvalidOperationException("Deposit failed. Please check account status and amount.");
        }
        return transaction;
    }

    public async Task<Transaction?> WithdrawAsync(Guid accountId, decimal amount, string? description = null)
    {
        var transaction = await ProcessWithdrawalAsync(accountId, amount, description);
        if (transaction == null)
        {
            throw new InvalidOperationException("Withdrawal failed. Insufficient funds or account not active.");
        }
        return transaction;
    }

    public async Task<(Transaction? fromTx, Transaction? toTx)> TransferAsync(Guid fromAccountId, Guid toAccountId, decimal amount, string? description = null, string? idempotencyKey = null)
    {
        // Idempotency check
        if (!string.IsNullOrEmpty(idempotencyKey))
        {
            var existingTransaction = await _context.Transactions
                .FirstOrDefaultAsync(t => t.ReferenceNumber == idempotencyKey);
            
            if (existingTransaction != null)
            {
                // Return existing transfer if found
                var fromTx = await _context.Transactions
                    .FirstOrDefaultAsync(t => t.AccountId == fromAccountId && t.ReferenceNumber == idempotencyKey);
                var toTx = await _context.Transactions
                    .FirstOrDefaultAsync(t => t.AccountId == toAccountId && t.ReferenceNumber == idempotencyKey);
                return (fromTx, toTx);
            }
        }

        var toAccount = await _context.Accounts.FindAsync(toAccountId);
        if (toAccount == null)
        {
            throw new InvalidOperationException("Destination account not found.");
        }

        var result = await ProcessTransferAsync(fromAccountId, toAccount.AccountNumber, amount, description);
        
        // Update reference number to idempotency key if provided
        if (!string.IsNullOrEmpty(idempotencyKey) && result.fromTx != null)
        {
            result.fromTx.ReferenceNumber = idempotencyKey;
            result.toTx!.ReferenceNumber = idempotencyKey;
            await _context.SaveChangesAsync();
        }

        return result;
    }

    public async Task<Transaction?> ProcessDepositAsync(Guid accountId, decimal amount, string? description = null)
    {
        if (amount <= 0) return null;

        var account = await _context.Accounts.FindAsync(accountId);
        if (account == null || account.Status != AccountStatus.Active) return null;

        var transaction = new Transaction
        {
            Id = Guid.NewGuid(),
            AccountId = accountId,
            Type = TransactionType.Deposit,
            Amount = amount,
            Currency = account.Currency,
            BalanceBefore = account.Balance,
            Description = description ?? "Deposit",
            ReferenceNumber = GenerateReferenceNumber(),
            Status = TransactionStatus.Pending,
            IpAddress = null
        };

        _context.Transactions.Add(transaction);
        await _context.SaveChangesAsync();

        // Process through ledger
        await _ledgerService.RecordTransactionAsync(transaction);

        // Update account balance
        account.Balance += amount;
        transaction.BalanceAfter = account.Balance;
        transaction.Status = TransactionStatus.Completed;
        transaction.UpdatedAt = DateTime.UtcNow;

        await _context.SaveChangesAsync();

        return transaction;
    }

    public async Task<Transaction?> ProcessWithdrawalAsync(Guid accountId, decimal amount, string? description = null)
    {
        if (amount <= 0) return null;

        var account = await _context.Accounts.FindAsync(accountId);
        if (account == null || account.Status != AccountStatus.Active) return null;

        if (account.Balance < amount) return null;

        var transaction = new Transaction
        {
            Id = Guid.NewGuid(),
            AccountId = accountId,
            Type = TransactionType.Withdrawal,
            Amount = amount,
            Currency = account.Currency,
            BalanceBefore = account.Balance,
            Description = description ?? "Withdrawal",
            ReferenceNumber = GenerateReferenceNumber(),
            Status = TransactionStatus.Pending
        };

        _context.Transactions.Add(transaction);
        await _context.SaveChangesAsync();

        // Process through ledger
        await _ledgerService.RecordTransactionAsync(transaction);

        // Update account balance
        account.Balance -= amount;
        transaction.BalanceAfter = account.Balance;
        transaction.Status = TransactionStatus.Completed;
        transaction.UpdatedAt = DateTime.UtcNow;

        await _context.SaveChangesAsync();

        return transaction;
    }

    public async Task<(Transaction? fromTx, Transaction? toTx)> ProcessTransferAsync(
        Guid fromAccountId, 
        string toAccountNumber, 
        decimal amount, 
        string? description = null)
    {
        if (amount <= 0) return (null, null);

        var fromAccount = await _context.Accounts.FindAsync(fromAccountId);
        if (fromAccount == null || fromAccount.Status != AccountStatus.Active) return (null, null);

        var toAccount = await _context.Accounts
            .FirstOrDefaultAsync(a => a.AccountNumber == toAccountNumber);
        if (toAccount == null || toAccount.Status != AccountStatus.Active) return (null, null);

        if (fromAccount.Balance < amount) return (null, null);

        // Create withdrawal transaction
        var fromTransaction = new Transaction
        {
            Id = Guid.NewGuid(),
            AccountId = fromAccountId,
            Type = TransactionType.Transfer,
            Amount = -amount,
            Currency = fromAccount.Currency,
            BalanceBefore = fromAccount.Balance,
            Description = description ?? $"Transfer to {toAccountNumber}",
            ReferenceNumber = GenerateReferenceNumber(),
            ToAccountNumber = toAccountNumber,
            Status = TransactionStatus.Pending
        };

        // Create deposit transaction
        var toTransaction = new Transaction
        {
            Id = Guid.NewGuid(),
            AccountId = toAccount.Id,
            Type = TransactionType.Transfer,
            Amount = amount,
            Currency = toAccount.Currency,
            BalanceBefore = toAccount.Balance,
            Description = description ?? $"Transfer from {fromAccount.AccountNumber}",
            ReferenceNumber = fromTransaction.ReferenceNumber,
            FromAccountNumber = fromAccount.AccountNumber,
            Status = TransactionStatus.Pending
        };

        _context.Transactions.Add(fromTransaction);
        _context.Transactions.Add(toTransaction);
        await _context.SaveChangesAsync();

        // Process through ledger
        await _ledgerService.RecordTransactionAsync(fromTransaction);
        await _ledgerService.RecordTransactionAsync(toTransaction);

        // Update balances
        fromAccount.Balance -= amount;
        fromTransaction.BalanceAfter = fromAccount.Balance;
        fromTransaction.Status = TransactionStatus.Completed;

        toAccount.Balance += amount;
        toTransaction.BalanceAfter = toAccount.Balance;
        toTransaction.Status = TransactionStatus.Completed;

        fromTransaction.UpdatedAt = DateTime.UtcNow;
        toTransaction.UpdatedAt = DateTime.UtcNow;

        await _context.SaveChangesAsync();

        return (fromTransaction, toTransaction);
    }

    public async Task<Transaction?> ProcessPaymentAsync(Guid accountId, decimal amount, string merchantName, string? description = null)
    {
        if (amount <= 0) return null;

        var account = await _context.Accounts.FindAsync(accountId);
        if (account == null || account.Status != AccountStatus.Active) return null;

        if (account.Balance < amount) return null;

        var transaction = new Transaction
        {
            Id = Guid.NewGuid(),
            AccountId = accountId,
            Type = TransactionType.Payment,
            Amount = -amount,
            Currency = account.Currency,
            BalanceBefore = account.Balance,
            Description = description ?? $"Payment to {merchantName}",
            ReferenceNumber = GenerateReferenceNumber(),
            Status = TransactionStatus.Pending
        };

        _context.Transactions.Add(transaction);
        await _context.SaveChangesAsync();

        // Process through ledger
        await _ledgerService.RecordTransactionAsync(transaction);

        // Update account balance
        account.Balance -= amount;
        transaction.BalanceAfter = account.Balance;
        transaction.Status = TransactionStatus.Completed;
        transaction.UpdatedAt = DateTime.UtcNow;

        await _context.SaveChangesAsync();

        // Check for compliance
        await _complianceService.CheckTransactionAsync(transaction);

        return transaction;
    }

    public async Task<Transaction?> ReverseTransactionAsync(Guid transactionId, string reason)
    {
        var originalTransaction = await _context.Transactions.FindAsync(transactionId);
        if (originalTransaction == null || originalTransaction.Status != TransactionStatus.Completed) return null;

        var account = await _context.Accounts.FindAsync(originalTransaction.AccountId);
        if (account == null) return null;

        var reversalTransaction = new Transaction
        {
            Id = Guid.NewGuid(),
            AccountId = originalTransaction.AccountId,
            Type = TransactionType.Reversal,
            Amount = -originalTransaction.Amount,
            Currency = originalTransaction.Currency,
            BalanceBefore = account.Balance,
            Description = $"Reversal: {reason}",
            ReferenceNumber = GenerateReferenceNumber(),
            Status = TransactionStatus.Pending
        };

        _context.Transactions.Add(reversalTransaction);
        await _context.SaveChangesAsync();

        // Process through ledger
        await _ledgerService.RecordTransactionAsync(reversalTransaction);

        // Update balance
        account.Balance += originalTransaction.Amount;
        reversalTransaction.BalanceAfter = account.Balance;
        reversalTransaction.Status = TransactionStatus.Completed;

        originalTransaction.Status = TransactionStatus.Reversed;
        originalTransaction.UpdatedAt = DateTime.UtcNow;
        reversalTransaction.UpdatedAt = DateTime.UtcNow;

        await _context.SaveChangesAsync();

        return reversalTransaction;
    }

    public async Task<bool> ValidateTransactionAsync(Guid accountId, decimal amount, TransactionType type)
    {
        var account = await _context.Accounts.FindAsync(accountId);
        if (account == null || account.Status != AccountStatus.Active) return false;

        // Check sufficient balance for debits
        if (type == TransactionType.Withdrawal || type == TransactionType.Payment || type == TransactionType.Transfer)
        {
            if (account.Balance < amount) return false;
        }

        // Check daily limits
        var today = DateTime.UtcNow.Date;
        var dailyTotal = await _context.Transactions
            .Where(t => t.AccountId == accountId && 
                       t.CreatedAt >= today && 
                       t.Status == TransactionStatus.Completed &&
                       (t.Type == TransactionType.Withdrawal || t.Type == TransactionType.Payment))
            .SumAsync(t => Math.Abs(t.Amount));

        if (dailyTotal + amount > 10000) return false; // $10,000 daily limit

        return true;
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
            .Where(t => t.IsFraudulent || t.Amount > 5000)
            .OrderByDescending(t => t.CreatedAt)
            .ToListAsync();
    }

    private static string GenerateReferenceNumber()
    {
        return $"TXN{DateTime.UtcNow:yyyyMMddHHmmss}{new Random().Next(1000, 9999)}";
    }
}
