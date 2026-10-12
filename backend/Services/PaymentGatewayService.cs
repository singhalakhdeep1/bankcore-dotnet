using BankingAPI.Data;
using BankingAPI.Models;
using Microsoft.EntityFrameworkCore;

namespace BankingAPI.Services;

public interface IPaymentGatewayService
{
    Task<PaymentResult> ProcessPaymentAsync(PaymentRequest request);
    Task<PaymentResult> RefundPaymentAsync(string transactionId, decimal? amount = null);
    Task<PaymentResult> GetPaymentStatusAsync(string transactionId);
    Task<List<PaymentLog>> GetPaymentLogsAsync(Guid accountId);
}

/// <summary>
/// Merchant payments against a bank account. The external gateway is simulated
/// (see <see cref="SimulateGatewayProcessing"/>); everything else - the account debit, the ledger entry,
/// the audit log and refunds - is real and goes through <see cref="ITransactionService"/>.
/// </summary>
public class PaymentGatewayService : IPaymentGatewayService
{
    private readonly ApplicationDbContext _context;
    private readonly ITransactionService _transactionService;
    private readonly IConfiguration _configuration;

    public PaymentGatewayService(
        ApplicationDbContext context,
        ITransactionService transactionService,
        IConfiguration configuration)
    {
        _context = context;
        _transactionService = transactionService;
        _configuration = configuration;
    }

    public async Task<PaymentResult> ProcessPaymentAsync(PaymentRequest request)
    {
        if (request.Amount <= 0 || decimal.Round(request.Amount, 2) != request.Amount)
        {
            return new PaymentResult { Success = false, ErrorMessage = "Invalid amount" };
        }

        var account = await _context.Accounts.FindAsync(request.AccountId);
        if (account == null || account.Status != AccountStatus.Active)
        {
            return new PaymentResult { Success = false, ErrorMessage = "Account not found or inactive" };
        }

        if (account.Balance + account.OverdraftLimit < request.Amount)
        {
            return new PaymentResult { Success = false, ErrorMessage = "Insufficient funds" };
        }

        var transactionId = $"PAY{DateTime.UtcNow:yyyyMMddHHmmss}{Guid.NewGuid().ToString("N")[..8].ToUpperInvariant()}";

        var paymentLog = new PaymentLog
        {
            Id = Guid.NewGuid(),
            AccountId = request.AccountId,
            TransactionId = transactionId,
            Amount = request.Amount,
            Currency = account.Currency,
            MerchantName = request.MerchantName,
            Status = "Processing",
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow
        };

        _context.PaymentLogs.Add(paymentLog);
        await _context.SaveChangesAsync();

        try
        {
            var gatewayResponse = await SimulateGatewayProcessing();

            if (!gatewayResponse.Success)
            {
                return await FailAsync(paymentLog, gatewayResponse.ErrorMessage ?? "Payment declined by gateway");
            }

            var transaction = await _transactionService.ProcessPaymentAsync(
                request.AccountId, request.Amount, request.MerchantName, request.Description);

            if (transaction == null)
            {
                return await FailAsync(paymentLog, "Payment rejected: insufficient funds or daily limit exceeded");
            }

            paymentLog.Status = "Completed";
            paymentLog.GatewayTransactionId = gatewayResponse.GatewayTransactionId;
            paymentLog.LedgerTransactionId = transaction.Id;
            paymentLog.UpdatedAt = DateTime.UtcNow;
            await _context.SaveChangesAsync();

            return new PaymentResult
            {
                Success = true,
                AccountId = request.AccountId,
                TransactionId = transactionId,
                GatewayTransactionId = gatewayResponse.GatewayTransactionId,
                Status = paymentLog.Status,
                Amount = request.Amount,
                Currency = account.Currency,
                ProcessedAt = DateTime.UtcNow
            };
        }
        catch (Exception ex)
        {
            paymentLog.Status = "Error";
            paymentLog.ErrorMessage = ex.Message;
            paymentLog.UpdatedAt = DateTime.UtcNow;
            await _context.SaveChangesAsync();

            return new PaymentResult
            {
                Success = false,
                AccountId = request.AccountId,
                TransactionId = transactionId,
                Status = paymentLog.Status,
                ErrorMessage = "Payment processing error"
            };
        }
    }

    /// <summary>Refunds a completed payment, in full or in part, by crediting the account.</summary>
    public async Task<PaymentResult> RefundPaymentAsync(string transactionId, decimal? amount = null)
    {
        var paymentLog = await _context.PaymentLogs.FirstOrDefaultAsync(pl => pl.TransactionId == transactionId);

        if (paymentLog == null || paymentLog.Status != "Completed")
        {
            return new PaymentResult { Success = false, ErrorMessage = "Payment not found or not completed" };
        }

        var refundAmount = amount ?? paymentLog.Amount;
        if (refundAmount <= 0 || refundAmount > paymentLog.Amount || decimal.Round(refundAmount, 2) != refundAmount)
        {
            return new PaymentResult { Success = false, ErrorMessage = "Refund amount must be between 0.01 and the payment amount" };
        }

        var refundTransaction = await _transactionService.ProcessRefundAsync(
            paymentLog.AccountId, refundAmount, $"payment {paymentLog.TransactionId} to {paymentLog.MerchantName}");

        if (refundTransaction == null)
        {
            return new PaymentResult { Success = false, ErrorMessage = "Refund processing failed" };
        }

        paymentLog.Status = "Refunded";
        paymentLog.RefundAmount = refundAmount;
        paymentLog.UpdatedAt = DateTime.UtcNow;
        await _context.SaveChangesAsync();

        return new PaymentResult
        {
            Success = true,
            AccountId = paymentLog.AccountId,
            TransactionId = paymentLog.TransactionId,
            Status = paymentLog.Status,
            Amount = refundAmount,
            Currency = paymentLog.Currency,
            ProcessedAt = paymentLog.UpdatedAt
        };
    }

    public async Task<PaymentResult> GetPaymentStatusAsync(string transactionId)
    {
        var paymentLog = await _context.PaymentLogs.AsNoTracking()
            .FirstOrDefaultAsync(pl => pl.TransactionId == transactionId);

        if (paymentLog == null)
        {
            return new PaymentResult { Success = false, ErrorMessage = "Payment not found" };
        }

        return new PaymentResult
        {
            Success = paymentLog.Status == "Completed",
            AccountId = paymentLog.AccountId,
            TransactionId = transactionId,
            GatewayTransactionId = paymentLog.GatewayTransactionId,
            Status = paymentLog.Status,
            Amount = paymentLog.Amount,
            Currency = paymentLog.Currency,
            ProcessedAt = paymentLog.UpdatedAt,
            ErrorMessage = paymentLog.ErrorMessage
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

    private async Task<PaymentResult> FailAsync(PaymentLog paymentLog, string message)
    {
        paymentLog.Status = "Failed";
        paymentLog.ErrorMessage = message;
        paymentLog.UpdatedAt = DateTime.UtcNow;
        await _context.SaveChangesAsync();

        return new PaymentResult
        {
            Success = false,
            AccountId = paymentLog.AccountId,
            TransactionId = paymentLog.TransactionId,
            Status = paymentLog.Status,
            ErrorMessage = message
        };
    }

    /// <summary>
    /// Stand-in for a real processor (Stripe, PayPal, ...). Approves every payment unless
    /// <c>Payments:SimulatedDeclineRate</c> (0-1) is configured, which declines that share at random.
    /// </summary>
    private async Task<GatewayResponse> SimulateGatewayProcessing()
    {
        await Task.Delay(200);

        var declineRate = _configuration.GetValue<double>("Payments:SimulatedDeclineRate");
        if (declineRate > 0 && Random.Shared.NextDouble() < declineRate)
        {
            return new GatewayResponse { Success = false, ErrorMessage = "Payment declined by gateway" };
        }

        return new GatewayResponse
        {
            Success = true,
            GatewayTransactionId = $"GW{Guid.NewGuid():N}",
            ProcessedAt = DateTime.UtcNow
        };
    }
}

public class PaymentRequest
{
    public Guid AccountId { get; set; }
    public decimal Amount { get; set; }
    public string Currency { get; set; } = "USD";
    public string MerchantName { get; set; } = string.Empty;
    public string? Description { get; set; }
}

public class PaymentResult
{
    public bool Success { get; set; }
    public Guid AccountId { get; set; }
    public string TransactionId { get; set; } = string.Empty;
    public string? GatewayTransactionId { get; set; }
    public decimal Amount { get; set; }
    public string Currency { get; set; } = "USD";
    public string? Status { get; set; }
    public DateTime? ProcessedAt { get; set; }
    public string? ErrorMessage { get; set; }
}

public class GatewayResponse
{
    public bool Success { get; set; }
    public string GatewayTransactionId { get; set; } = string.Empty;
    public DateTime ProcessedAt { get; set; }
    public string? ErrorMessage { get; set; }
}
