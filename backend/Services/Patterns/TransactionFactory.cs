using System;
using System.Collections.Generic;

namespace BankCore.Services.Patterns
{
    /// <summary>
    /// Factory Pattern for Transaction Creation
    /// Creates different types of transactions based on transaction type
    /// </summary>
    public interface ITransactionFactory
    {
        ITransaction CreateTransaction(TransactionType type, TransactionContext context);
    }

    /// <summary>
    /// Transaction interface
    /// </summary>
    public interface ITransaction
    {
        string TransactionId { get; }
        TransactionType Type { get; }
        decimal Amount { get; }
        string AccountId { get; }
        DateTime CreatedAt { get; }
        Task<bool> ValidateAsync();
        Task<bool> ProcessAsync();
        Task<bool> ReverseAsync();
    }

    /// <summary>
    /// Transaction type
    /// </summary>
    public enum TransactionType
    {
        Deposit,
        Withdrawal,
        Transfer,
        Payment,
        Refund,
        Interest,
        Fee
    }

    /// <summary>
    /// Transaction context
    /// </summary>
    public class TransactionContext
    {
        public string AccountId { get; set; } = string.Empty;
        public decimal Amount { get; set; }
        public string Currency { get; set; } = string.Empty;
        public string? ReferenceNumber { get; set; }
        public string? Description { get; set; }
        public string? DestinationAccountId { get; set; }
        public Dictionary<string, object> Metadata { get; set; } = new();
    }

    /// <summary>
    /// Base transaction
    /// </summary>
    public abstract class BaseTransaction : ITransaction
    {
        protected readonly ILogger _logger;
        protected readonly TransactionContext _context;

        public string TransactionId { get; protected set; }
        public TransactionType Type { get; protected set; }
        public decimal Amount => _context.Amount;
        public string AccountId => _context.AccountId;
        public DateTime CreatedAt { get; protected set; }

        protected BaseTransaction(TransactionContext context, ILogger logger)
        {
            _context = context;
            _logger = logger;
            TransactionId = GenerateTransactionId();
            CreatedAt = DateTime.UtcNow;
        }

        public abstract Task<bool> ValidateAsync();
        public abstract Task<bool> ProcessAsync();
        public abstract Task<bool> ReverseAsync();

        protected virtual string GenerateTransactionId()
        {
            return $"{Type.ToString().ToLower()}_{DateTime.UtcNow:yyyyMMddHHmmss}_{Guid.NewGuid():N}";
        }
    }

    /// <summary>
    /// Deposit transaction
    /// </summary>
    public class DepositTransaction : BaseTransaction
    {
        public DepositTransaction(TransactionContext context, ILogger<DepositTransaction> logger)
            : base(context, logger)
        {
            Type = TransactionType.Deposit;
        }

        public override async Task<bool> ValidateAsync()
        {
            _logger.LogInformation($"Validating deposit transaction: {TransactionId}");

            // Validate amount
            if (Amount <= 0)
            {
                _logger.LogError($"Invalid deposit amount: {Amount}");
                return false;
            }

            // Validate account
            if (string.IsNullOrEmpty(AccountId))
            {
                _logger.LogError("Account ID is required");
                return false;
            }

            return await Task.FromResult(true);
        }

        public override async Task<bool> ProcessAsync()
        {
            _logger.LogInformation($"Processing deposit transaction: {TransactionId}");

            try
            {
                // Simulate deposit processing
                await Task.Delay(50);

                _logger.LogInformation($"Deposit processed successfully: {TransactionId}");
                return true;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, $"Deposit processing failed: {TransactionId}");
                return false;
            }
        }

        public override async Task<bool> ReverseAsync()
        {
            _logger.LogInformation($"Reversing deposit transaction: {TransactionId}");

            try
            {
                // Simulate reversal
                await Task.Delay(50);

                _logger.LogInformation($"Deposit reversed successfully: {TransactionId}");
                return true;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, $"Deposit reversal failed: {TransactionId}");
                return false;
            }
        }
    }

    /// <summary>
    /// Withdrawal transaction
    /// </summary>
    public class WithdrawalTransaction : BaseTransaction
    {
        public WithdrawalTransaction(TransactionContext context, ILogger<WithdrawalTransaction> logger)
            : base(context, logger)
        {
            Type = TransactionType.Withdrawal;
        }

        public override async Task<bool> ValidateAsync()
        {
            _logger.LogInformation($"Validating withdrawal transaction: {TransactionId}");

            // Validate amount
            if (Amount <= 0)
            {
                _logger.LogError($"Invalid withdrawal amount: {Amount}");
                return false;
            }

            // Validate account
            if (string.IsNullOrEmpty(AccountId))
            {
                _logger.LogError("Account ID is required");
                return false;
            }

            // Check sufficient balance (would need account service)
            // For now, assume validation passes

            return await Task.FromResult(true);
        }

        public override async Task<bool> ProcessAsync()
        {
            _logger.LogInformation($"Processing withdrawal transaction: {TransactionId}");

            try
            {
                // Simulate withdrawal processing
                await Task.Delay(50);

                _logger.LogInformation($"Withdrawal processed successfully: {TransactionId}");
                return true;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, $"Withdrawal processing failed: {TransactionId}");
                return false;
            }
        }

        public override async Task<bool> ReverseAsync()
        {
            _logger.LogInformation($"Reversing withdrawal transaction: {TransactionId}");

            try
            {
                // Simulate reversal
                await Task.Delay(50);

                _logger.LogInformation($"Withdrawal reversed successfully: {TransactionId}");
                return true;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, $"Withdrawal reversal failed: {TransactionId}");
                return false;
            }
        }
    }

    /// <summary>
    /// Transfer transaction
    /// </summary>
    public class TransferTransaction : BaseTransaction
    {
        public TransferTransaction(TransactionContext context, ILogger<TransferTransaction> logger)
            : base(context, logger)
        {
            Type = TransactionType.Transfer;
        }

        public override async Task<bool> ValidateAsync()
        {
            _logger.LogInformation($"Validating transfer transaction: {TransactionId}");

            // Validate amount
            if (Amount <= 0)
            {
                _logger.LogError($"Invalid transfer amount: {Amount}");
                return false;
            }

            // Validate accounts
            if (string.IsNullOrEmpty(AccountId))
            {
                _logger.LogError("Source account ID is required");
                return false;
            }

            if (string.IsNullOrEmpty(_context.DestinationAccountId))
            {
                _logger.LogError("Destination account ID is required");
                return false;
            }

            // Cannot transfer to same account
            if (AccountId == _context.DestinationAccountId)
            {
                _logger.LogError("Cannot transfer to same account");
                return false;
            }

            return await Task.FromResult(true);
        }

        public override async Task<bool> ProcessAsync()
        {
            _logger.LogInformation($"Processing transfer transaction: {TransactionId}");

            try
            {
                // Simulate transfer processing
                await Task.Delay(100);

                _logger.LogInformation($"Transfer processed successfully: {TransactionId}");
                return true;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, $"Transfer processing failed: {TransactionId}");
                return false;
            }
        }

        public override async Task<bool> ReverseAsync()
        {
            _logger.LogInformation($"Reversing transfer transaction: {TransactionId}");

            try
            {
                // Simulate reversal (reverse both accounts)
                await Task.Delay(100);

                _logger.LogInformation($"Transfer reversed successfully: {TransactionId}");
                return true;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, $"Transfer reversal failed: {TransactionId}");
                return false;
            }
        }
    }

    /// <summary>
    /// Transaction factory implementation
    /// </summary>
    public class TransactionFactory : ITransactionFactory
    {
        private readonly IServiceProvider _serviceProvider;
        private readonly ILogger<TransactionFactory> _logger;

        public TransactionFactory(
            IServiceProvider serviceProvider,
            ILogger<TransactionFactory> logger)
        {
            _serviceProvider = serviceProvider;
            _logger = logger;
        }

        public ITransaction CreateTransaction(TransactionType type, TransactionContext context)
        {
            _logger.LogInformation($"Creating transaction of type: {type}");

            return type switch
            {
                TransactionType.Deposit => ActivatorUtilities.CreateInstance<DepositTransaction>(_serviceProvider, context),
                TransactionType.Withdrawal => ActivatorUtilities.CreateInstance<WithdrawalTransaction>(_serviceProvider, context),
                TransactionType.Transfer => ActivatorUtilities.CreateInstance<TransferTransaction>(_serviceProvider, context),
                _ => throw new ArgumentException($"Unsupported transaction type: {type}")
            };
        }
    }
}
