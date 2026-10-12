using BankCore.Services.ErrorHandling;
using BankCore.Services.Saga;
using BankingAPI.Data;
using BankingAPI.Models;
using BankingAPI.Services.Security;
using Microsoft.EntityFrameworkCore;
using Gateway = BankCore.Services.Patterns;

namespace BankingAPI.Services;

public interface IPaymentGatewayService
{
    /// <summary>Pays a merchant from the account balance.</summary>
    Task<PaymentResult> ProcessPaymentAsync(PaymentRequest request);

    /// <summary>Adds money to the account by charging a stored card/PayPal method through the payment gateway.</summary>
    Task<PaymentResult> TopUpFromCardAsync(CardTopUpRequest request);

    Task<PaymentResult> RefundPaymentAsync(string transactionId, decimal? amount = null);
    Task<PaymentResult> GetPaymentStatusAsync(string transactionId);
    Task<List<PaymentLog>> GetPaymentLogsAsync(Guid accountId);
}

/// <summary>
/// Payments and refunds, run as sagas so a failure after money has moved is always compensated:
/// the ledger posting is reversed, or the gateway charge is refunded.
/// Card charges go through <see cref="Gateway.IPaymentStrategy"/> (Stripe or PayPal; simulated without credentials)
/// wrapped in the retry / circuit-breaker / timeout policies of <see cref="ErrorHandlingService"/>.
/// </summary>
public class PaymentGatewayService : IPaymentGatewayService
{
    private const string KindMerchant = "MerchantPayment";
    private const string KindTopUp = "CardTopUp";

    private readonly ApplicationDbContext _context;
    private readonly ITransactionService _transactionService;
    private readonly IComplianceService _compliance;
    private readonly SagaManager _saga;
    private readonly Gateway.PaymentStrategyFactory _strategies;
    private readonly ErrorHandlingService _resilience;
    private readonly IAuditService _audit;
    private readonly IConfiguration _configuration;
    private readonly ILogger<PaymentGatewayService> _logger;

    public PaymentGatewayService(
        ApplicationDbContext context,
        ITransactionService transactionService,
        IComplianceService compliance,
        SagaManager saga,
        Gateway.PaymentStrategyFactory strategies,
        ErrorHandlingService resilience,
        IAuditService audit,
        IConfiguration configuration,
        ILogger<PaymentGatewayService> logger)
    {
        _context = context;
        _transactionService = transactionService;
        _compliance = compliance;
        _saga = saga;
        _strategies = strategies;
        _resilience = resilience;
        _audit = audit;
        _configuration = configuration;
        _logger = logger;
    }

    // ------------------------------------------------------------------ merchant payment

    public async Task<PaymentResult> ProcessPaymentAsync(PaymentRequest request)
    {
        if (request.Amount <= 0 || decimal.Round(request.Amount, 2) != request.Amount)
        {
            return new PaymentResult { Success = false, ErrorMessage = "Invalid amount" };
        }

        var state = new PaymentFlow
        {
            Kind = KindMerchant,
            AccountId = request.AccountId,
            Amount = request.Amount,
            Merchant = request.MerchantName,
            Description = request.Description,
            Reference = NewReference()
        };

        var saga = new SagaDefinition<PaymentFlow>
        {
            Name = "MerchantPayment",
            Steps = new List<SagaStep<PaymentFlow>>
            {
                new() { Name = "Validate", Retryable = false, Execute = ValidateMerchantPaymentAsync },
                new() { Name = "RecordPayment", Retryable = false, Execute = CreateLogAsync, Compensate = MarkLogFailedAsync },
                new() { Name = "PostLedger", Retryable = false, Execute = PostMerchantPaymentAsync, Compensate = ReverseLedgerAsync },
                new() { Name = "Complete", Retryable = true, MaxRetries = 3, Execute = CompleteAsync }
            }
        };

        return await RunAsync(saga, state);
    }

    private async Task<PaymentFlow> ValidateMerchantPaymentAsync(PaymentFlow flow)
    {
        var account = await LoadActiveAccountAsync(flow);
        if (account.Balance + account.OverdraftLimit < flow.Amount) throw Abort(flow, "Insufficient funds");

        var decision = await _compliance.EvaluateAsync(account.Id, flow.Amount, TransactionType.Payment, flow.Merchant);
        if (!decision.Allowed) throw Abort(flow, decision.Reason ?? "The payment was refused by compliance.");

        flow.Currency = account.Currency;
        return flow;
    }

    private async Task<PaymentFlow> PostMerchantPaymentAsync(PaymentFlow flow)
    {
        Transaction? transaction;
        try
        {
            transaction = await _transactionService.ProcessPaymentAsync(flow.AccountId, flow.Amount, flow.Merchant, flow.Description);
        }
        catch (TransactionRejectedException ex)
        {
            throw Abort(flow, ex.Message);
        }

        flow.Ledger = transaction ?? throw Abort(flow, "Payment rejected: insufficient funds or daily limit exceeded");
        flow.Log!.LedgerTransactionId = transaction.Id;
        return flow;
    }

    // ------------------------------------------------------------------ card top-up

    public async Task<PaymentResult> TopUpFromCardAsync(CardTopUpRequest request)
    {
        if (request.Amount <= 0 || decimal.Round(request.Amount, 2) != request.Amount)
        {
            return new PaymentResult { Success = false, ErrorMessage = "Invalid amount" };
        }

        var state = new PaymentFlow
        {
            Kind = KindTopUp,
            AccountId = request.AccountId,
            UserId = request.UserId,
            Amount = request.Amount,
            Merchant = "Card top-up",
            Description = request.Description ?? "Card top-up",
            PaymentMethodId = request.PaymentMethodId,
            Reference = NewReference()
        };

        var saga = new SagaDefinition<PaymentFlow>
        {
            Name = "CardTopUp",
            Steps = new List<SagaStep<PaymentFlow>>
            {
                new() { Name = "Validate", Retryable = false, Execute = ValidateTopUpAsync },
                new() { Name = "RecordPayment", Retryable = false, Execute = CreateLogAsync, Compensate = MarkLogFailedAsync },
                new() { Name = "ChargeCard", Retryable = false, Execute = ChargeAsync, Compensate = RefundChargeAsync },
                new() { Name = "CreditAccount", Retryable = false, Execute = CreditTopUpAsync, Compensate = ReverseLedgerAsync },
                new() { Name = "Complete", Retryable = true, MaxRetries = 3, Execute = CompleteAsync }
            }
        };

        return await RunAsync(saga, state);
    }

    private async Task<PaymentFlow> ValidateTopUpAsync(PaymentFlow flow)
    {
        var account = await LoadActiveAccountAsync(flow);

        var method = await _context.PaymentMethods
            .FirstOrDefaultAsync(m => m.Id == flow.PaymentMethodId && m.UserId == flow.UserId && m.RemovedAt == null);
        if (method == null) throw Abort(flow, "Payment method not found");

        var strategy = _strategies.GetStrategy(method.Provider);
        if (!strategy.SupportsCurrency(account.Currency))
        {
            throw Abort(flow, $"{strategy.ProviderName} does not support {account.Currency}");
        }

        var decision = await _compliance.EvaluateAsync(account.Id, flow.Amount, TransactionType.Deposit);
        if (!decision.Allowed) throw Abort(flow, decision.Reason ?? "The payment was refused by compliance.");

        flow.Currency = account.Currency;
        flow.Method = method;
        flow.Strategy = strategy;
        return flow;
    }

    private async Task<PaymentFlow> ChargeAsync(PaymentFlow flow)
    {
        var strategy = flow.Strategy!;
        var charge = await _resilience.ExecuteWithResilienceAsync(() => strategy.ProcessPaymentAsync(new Gateway.PaymentRequest
        {
            AccountId = flow.AccountId.ToString(),
            Amount = flow.Amount,
            Currency = flow.Currency,
            ReferenceNumber = flow.Reference,
            Description = flow.Description ?? "Card top-up",
            PaymentMethodToken = flow.Method!.Token
        }));

        if (!charge.Success)
        {
            throw Abort(flow, string.IsNullOrWhiteSpace(charge.Message) ? "The card was declined." : charge.Message);
        }

        flow.Charge = charge;
        flow.Log!.GatewayTransactionId = charge.TransactionId;
        flow.Log.Fee = charge.Fee;
        flow.Log.ProviderMode = charge.Metadata.GetValueOrDefault("mode");
        await _context.SaveChangesAsync();
        return flow;
    }

    private async Task<PaymentFlow> CreditTopUpAsync(PaymentFlow flow)
    {
        Transaction? transaction;
        try
        {
            transaction = await _transactionService.ProcessCardTopUpAsync(
                flow.AccountId, flow.Amount, flow.Description ?? "Card top-up", flow.Reference);
        }
        catch (TransactionRejectedException ex)
        {
            throw Abort(flow, ex.Message);
        }

        flow.Ledger = transaction ?? throw Abort(flow, "The account could not be credited");
        flow.Log!.LedgerTransactionId = transaction.Id;
        return flow;
    }

    /// <summary>Compensation: the account could not be credited after the card was charged, so refund the card.</summary>
    private async Task RefundChargeAsync(PaymentFlow flow)
    {
        if (flow.Charge == null || flow.Strategy == null) return;

        var refund = await flow.Strategy.ProcessRefundAsync(new Gateway.RefundRequest
        {
            TransactionId = flow.Charge.TransactionId,
            Amount = flow.Amount,
            Reason = "Account could not be credited"
        });

        if (!refund.Success)
        {
            // Needs a human: money was taken from the card but not credited and the automatic refund failed.
            _logger.LogCritical("Compensation failed: could not refund gateway charge {Charge} for payment {Reference}",
                flow.Charge.TransactionId, flow.Reference);
        }
    }

    // ------------------------------------------------------------------ shared saga steps

    private async Task<Account> LoadActiveAccountAsync(PaymentFlow flow)
    {
        var account = await _context.Accounts.AsNoTracking().FirstOrDefaultAsync(a => a.Id == flow.AccountId);
        if (account == null || account.Status != AccountStatus.Active) throw Abort(flow, "Account not found or inactive");
        return account;
    }

    private async Task<PaymentFlow> CreateLogAsync(PaymentFlow flow)
    {
        flow.Log = new PaymentLog
        {
            Id = Guid.NewGuid(),
            AccountId = flow.AccountId,
            TransactionId = flow.Reference,
            Kind = flow.Kind,
            Provider = flow.Strategy?.ProviderName,
            ProviderMode = flow.Strategy == null ? null : (flow.Strategy.IsLive ? "live" : "simulated"),
            PaymentMethodId = flow.PaymentMethodId,
            Amount = flow.Amount,
            Currency = flow.Currency,
            MerchantName = flow.Merchant,
            Status = "Processing",
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow
        };

        _context.PaymentLogs.Add(flow.Log);
        await _context.SaveChangesAsync();
        return flow;
    }

    private async Task MarkLogFailedAsync(PaymentFlow flow)
    {
        if (flow.Log == null) return;

        flow.Log.Status = flow.Failure == null ? "Error" : "Failed";
        flow.Log.ErrorMessage = flow.Failure ?? "Payment processing error";
        flow.Log.UpdatedAt = DateTime.UtcNow;
        await _context.SaveChangesAsync();
    }

    /// <summary>Compensation: undo the ledger posting when a later step fails.</summary>
    private async Task ReverseLedgerAsync(PaymentFlow flow)
    {
        if (flow.Ledger == null) return;
        await _transactionService.ReverseTransactionAsync(flow.Ledger.Id, "Payment could not be completed");
    }

    private async Task<PaymentFlow> CompleteAsync(PaymentFlow flow)
    {
        flow.Log!.Status = "Completed";
        flow.Log.UpdatedAt = DateTime.UtcNow;
        await _context.SaveChangesAsync();
        return flow;
    }

    private async Task<PaymentResult> RunAsync(SagaDefinition<PaymentFlow> saga, PaymentFlow state)
    {
        try
        {
            var done = await _saga.ExecuteAsync(saga, state);
            await _audit.LogAsync("Payment.Completed", state.UserId == Guid.Empty ? null : state.UserId, "PaymentLog",
                done.Log!.Id.ToString(), $"{done.Kind} {done.Amount:N2} {done.Currency} ref {done.Reference}");

            return new PaymentResult
            {
                Success = true,
                AccountId = done.AccountId,
                TransactionId = done.Reference,
                GatewayTransactionId = done.Log.GatewayTransactionId,
                Provider = done.Log.Provider,
                Mode = done.Log.ProviderMode,
                Fee = done.Log.Fee,
                Status = done.Log.Status,
                Amount = done.Amount,
                Currency = done.Currency,
                ProcessedAt = DateTime.UtcNow
            };
        }
        catch (SagaAbortException ex)
        {
            return new PaymentResult
            {
                Success = false,
                AccountId = state.AccountId,
                TransactionId = state.Log == null ? string.Empty : state.Reference,
                Status = state.Log == null ? null : "Failed",
                ErrorMessage = ex.Message
            };
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Payment saga {Saga} failed unexpectedly", saga.Name);
            return new PaymentResult
            {
                Success = false,
                AccountId = state.AccountId,
                TransactionId = state.Log == null ? string.Empty : state.Reference,
                Status = state.Log == null ? null : "Error",
                ErrorMessage = "Payment processing error"
            };
        }
    }

    private static SagaAbortException Abort(PaymentFlow flow, string message)
    {
        flow.Failure = message;
        return new SagaAbortException(message);
    }

    // ------------------------------------------------------------------ refunds

    /// <summary>
    /// Refunds a completed payment. Merchant payments are credited back (full or partial);
    /// card top-ups are taken back out of the account and refunded to the card (full only).
    /// </summary>
    public async Task<PaymentResult> RefundPaymentAsync(string transactionId, decimal? amount = null)
    {
        var log = await _context.PaymentLogs.FirstOrDefaultAsync(pl => pl.TransactionId == transactionId);

        if (log == null || log.Status != "Completed")
        {
            return new PaymentResult { Success = false, ErrorMessage = "Payment not found or not completed" };
        }

        var refundAmount = amount ?? log.Amount;
        if (refundAmount <= 0 || refundAmount > log.Amount || decimal.Round(refundAmount, 2) != refundAmount)
        {
            return new PaymentResult { Success = false, ErrorMessage = "Refund amount must be between 0.01 and the payment amount" };
        }

        return log.Kind == KindTopUp
            ? await RefundTopUpAsync(log, refundAmount)
            : await RefundMerchantPaymentAsync(log, refundAmount);
    }

    private async Task<PaymentResult> RefundMerchantPaymentAsync(PaymentLog log, decimal refundAmount)
    {
        var refundTransaction = await _transactionService.ProcessRefundAsync(
            log.AccountId, refundAmount, $"payment {log.TransactionId} to {log.MerchantName}");

        if (refundTransaction == null)
        {
            return new PaymentResult { Success = false, ErrorMessage = "Refund processing failed" };
        }

        log.Status = "Refunded";
        log.RefundAmount = refundAmount;
        log.UpdatedAt = DateTime.UtcNow;
        await _context.SaveChangesAsync();

        return RefundResult(log, refundAmount);
    }

    private async Task<PaymentResult> RefundTopUpAsync(PaymentLog log, decimal refundAmount)
    {
        if (refundAmount != log.Amount)
        {
            return new PaymentResult { Success = false, ErrorMessage = "Card top-ups can only be refunded in full" };
        }

        if (log.LedgerTransactionId == null || log.GatewayTransactionId == null)
        {
            return new PaymentResult { Success = false, ErrorMessage = "This top-up cannot be refunded" };
        }

        var strategy = _strategies.GetStrategy(log.Provider ?? "Stripe");
        var flow = new PaymentFlow { Kind = KindTopUp, AccountId = log.AccountId, Amount = refundAmount, Log = log, Strategy = strategy, Reference = log.TransactionId };

        var saga = new SagaDefinition<PaymentFlow>
        {
            Name = "CardTopUpRefund",
            Steps = new List<SagaStep<PaymentFlow>>
            {
                new()
                {
                    Name = "DebitAccount",
                    Retryable = false,
                    Execute = async f =>
                    {
                        Transaction? reversal;
                        try
                        {
                            reversal = await _transactionService.ReverseTransactionAsync(log.LedgerTransactionId!.Value, "Refund of card top-up");
                        }
                        catch (InvalidOperationException ex)
                        {
                            throw Abort(f, ex.Message);
                        }

                        f.Ledger = reversal ?? throw Abort(f, "The account no longer holds enough funds to refund this top-up");
                        return f;
                    },
                    // Funds were taken back but the card refund failed: put the money back.
                    Compensate = async f =>
                    {
                        if (f.Ledger != null)
                        {
                            await _transactionService.ProcessCardTopUpAsync(f.AccountId, f.Amount, "Refund failed, funds restored", $"RST{Guid.NewGuid():N}"[..20]);
                        }
                    }
                },
                new()
                {
                    Name = "RefundCard",
                    Retryable = false,
                    Execute = async f =>
                    {
                        var refund = await _resilience.ExecuteWithResilienceAsync(() => strategy.ProcessRefundAsync(new Gateway.RefundRequest
                        {
                            TransactionId = log.GatewayTransactionId!,
                            Amount = f.Amount,
                            Reason = "Requested by customer"
                        }));

                        if (!refund.Success) throw Abort(f, "The card refund was rejected by the payment provider");
                        return f;
                    }
                }
            }
        };

        try
        {
            await _saga.ExecuteAsync(saga, flow);
        }
        catch (SagaAbortException ex)
        {
            return new PaymentResult { Success = false, ErrorMessage = ex.Message };
        }

        log.Status = "Refunded";
        log.RefundAmount = refundAmount;
        log.UpdatedAt = DateTime.UtcNow;
        await _context.SaveChangesAsync();
        await _audit.LogAsync("Payment.Refunded", null, "PaymentLog", log.Id.ToString(), $"Top-up {log.TransactionId} refunded to card");

        return RefundResult(log, refundAmount);
    }

    private static PaymentResult RefundResult(PaymentLog log, decimal amount) => new()
    {
        Success = true,
        AccountId = log.AccountId,
        TransactionId = log.TransactionId,
        Provider = log.Provider,
        Mode = log.ProviderMode,
        Status = log.Status,
        Amount = amount,
        Currency = log.Currency,
        ProcessedAt = log.UpdatedAt
    };

    // ------------------------------------------------------------------ reads

    public async Task<PaymentResult> GetPaymentStatusAsync(string transactionId)
    {
        var log = await _context.PaymentLogs.AsNoTracking().FirstOrDefaultAsync(pl => pl.TransactionId == transactionId);

        if (log == null)
        {
            return new PaymentResult { Success = false, ErrorMessage = "Payment not found" };
        }

        return new PaymentResult
        {
            Success = log.Status == "Completed",
            AccountId = log.AccountId,
            TransactionId = transactionId,
            GatewayTransactionId = log.GatewayTransactionId,
            Provider = log.Provider,
            Mode = log.ProviderMode,
            Fee = log.Fee,
            Status = log.Status,
            Amount = log.Amount,
            Currency = log.Currency,
            ProcessedAt = log.UpdatedAt,
            ErrorMessage = log.ErrorMessage
        };
    }

    public async Task<List<PaymentLog>> GetPaymentLogsAsync(Guid accountId)
    {
        return await _context.PaymentLogs
            .AsNoTracking()
            .Where(pl => pl.AccountId == accountId)
            .OrderByDescending(pl => pl.CreatedAt)
            .ToListAsync();
    }

    private static string NewReference() =>
        $"PAY{DateTime.UtcNow:yyyyMMddHHmmss}{Guid.NewGuid().ToString("N")[..8].ToUpperInvariant()}";

    /// <summary>Everything the payment saga's steps share.</summary>
    public class PaymentFlow
    {
        public string Kind { get; set; } = KindMerchant;
        public Guid AccountId { get; set; }
        public Guid UserId { get; set; }
        public decimal Amount { get; set; }
        public string Currency { get; set; } = "USD";
        public string Merchant { get; set; } = string.Empty;
        public string? Description { get; set; }
        public Guid? PaymentMethodId { get; set; }
        public string Reference { get; set; } = string.Empty;

        public PaymentMethod? Method { get; set; }
        public Gateway.IPaymentStrategy? Strategy { get; set; }
        public PaymentLog? Log { get; set; }
        public Gateway.PaymentResult? Charge { get; set; }
        public Transaction? Ledger { get; set; }
        public string? Failure { get; set; }
    }
}

public class PaymentRequest
{
    public Guid AccountId { get; set; }
    public Guid UserId { get; set; }
    public decimal Amount { get; set; }
    public string Currency { get; set; } = "USD";
    public string MerchantName { get; set; } = string.Empty;
    public string? Description { get; set; }
}

public class CardTopUpRequest
{
    public Guid AccountId { get; set; }
    public Guid UserId { get; set; }
    public decimal Amount { get; set; }
    public Guid PaymentMethodId { get; set; }
    public string? Description { get; set; }
}

public class PaymentResult
{
    public bool Success { get; set; }
    public Guid AccountId { get; set; }
    public string TransactionId { get; set; } = string.Empty;
    public string? GatewayTransactionId { get; set; }
    public string? Provider { get; set; }

    /// <summary>live or simulated.</summary>
    public string? Mode { get; set; }

    public decimal Fee { get; set; }
    public decimal Amount { get; set; }
    public string Currency { get; set; } = "USD";
    public string? Status { get; set; }
    public DateTime? ProcessedAt { get; set; }
    public string? ErrorMessage { get; set; }
}
