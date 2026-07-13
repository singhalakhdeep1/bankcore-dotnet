using BankingAPI.Data;
using BankingAPI.Models;
using BankingAPI.DTOs;
using Microsoft.EntityFrameworkCore;
using System.Text.Json;

namespace BankingAPI.Services;

public interface IPaymentGatewayService
{
    Task<PaymentResult> ProcessPaymentAsync(PaymentRequest request);
    Task<PaymentResult> RefundPaymentAsync(string transactionId, decimal? amount = null);
    Task<PaymentResult> GetPaymentStatusAsync(string transactionId);
    Task<List<PaymentLog>> GetPaymentLogsAsync(Guid accountId);
}

public class PaymentGatewayService : IPaymentGatewayService
{
    private readonly ApplicationDbContext _context;
    private readonly ITransactionService _transactionService;
    private readonly HttpClient _httpClient;

    public PaymentGatewayService(
        ApplicationDbContext context,
        ITransactionService transactionService,
        IHttpClientFactory httpClientFactory)
    {
        _context = context;
        _transactionService = transactionService;
        _httpClient = httpClientFactory.CreateClient();
    }

    public async Task<PaymentResult> ProcessPaymentAsync(PaymentRequest request)
    {
        // Validate request
        if (request.Amount <= 0)
        {
            return new PaymentResult { Success = false, ErrorMessage = "Invalid amount" };
        }

        var account = await _context.Accounts.FindAsync(request.AccountId);
        if (account == null || account.Status != AccountStatus.Active)
        {
            return new PaymentResult { Success = false, ErrorMessage = "Account not found or inactive" };
        }

        if (account.Balance < request.Amount)
        {
            return new PaymentResult { Success = false, ErrorMessage = "Insufficient funds" };
        }

        // Generate transaction ID
        var transactionId = $"PAY{DateTime.UtcNow:yyyyMMddHHmmss}{new Random().Next(1000, 9999)}";

        // Log payment attempt
        var paymentLog = new PaymentLog
        {
            Id = Guid.NewGuid(),
            AccountId = request.AccountId,
            TransactionId = transactionId,
            Amount = request.Amount,
            Currency = account.Currency,
            MerchantName = request.MerchantName,
            Status = "Processing",
            CreatedAt = DateTime.UtcNow
        };

        _context.PaymentLogs.Add(paymentLog);
        await _context.SaveChangesAsync();

        try
        {
            // Simulate payment gateway processing
            var gatewayResponse = await SimulateGatewayProcessing(request, transactionId);

            if (gatewayResponse.Success)
            {
                // Process the actual transaction
                var transaction = await _transactionService.ProcessPaymentAsync(
                    request.AccountId,
                    request.Amount,
                    request.MerchantName,
                    request.Description
                );

                if (transaction != null)
                {
                    paymentLog.Status = "Completed";
                    paymentLog.GatewayTransactionId = gatewayResponse.GatewayTransactionId;
                    paymentLog.UpdatedAt = DateTime.UtcNow;

                    await _context.SaveChangesAsync();

                    return new PaymentResult
                    {
                        Success = true,
                        TransactionId = transactionId,
                        GatewayTransactionId = gatewayResponse.GatewayTransactionId,
                        Amount = request.Amount,
                        Currency = account.Currency,
                        ProcessedAt = DateTime.UtcNow
                    };
                }
            }

            paymentLog.Status = "Failed";
            paymentLog.ErrorMessage = gatewayResponse.ErrorMessage;
            paymentLog.UpdatedAt = DateTime.UtcNow;
            await _context.SaveChangesAsync();

            return new PaymentResult
            {
                Success = false,
                TransactionId = transactionId,
                ErrorMessage = gatewayResponse.ErrorMessage
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
                TransactionId = transactionId,
                ErrorMessage = "Payment processing error"
            };
        }
    }

    public async Task<PaymentResult> RefundPaymentAsync(string transactionId, decimal? amount = null)
    {
        var paymentLog = await _context.PaymentLogs
            .FirstOrDefaultAsync(pl => pl.TransactionId == transactionId);

        if (paymentLog == null || paymentLog.Status != "Completed")
        {
            return new PaymentResult { Success = false, ErrorMessage = "Payment not found or not completed" };
        }

        var refundAmount = amount ?? paymentLog.Amount;

        // Process refund transaction
        var refundTransaction = await _transactionService.ReverseTransactionAsync(
            Guid.Parse(paymentLog.TransactionId.Replace("PAY", "").Substring(0, 32)),
            "Refund requested"
        );

        if (refundTransaction != null)
        {
            paymentLog.Status = "Refunded";
            paymentLog.RefundAmount = refundAmount;
            paymentLog.UpdatedAt = DateTime.UtcNow;
            await _context.SaveChangesAsync();

            return new PaymentResult
            {
                Success = true,
                TransactionId = $"REF{DateTime.UtcNow:yyyyMMddHHmmss}{new Random().Next(1000, 9999)}",
                Amount = refundAmount,
                Currency = paymentLog.Currency,
                ProcessedAt = DateTime.UtcNow
            };
        }

        return new PaymentResult { Success = false, ErrorMessage = "Refund processing failed" };
    }

    public async Task<PaymentResult> GetPaymentStatusAsync(string transactionId)
    {
        var paymentLog = await _context.PaymentLogs
            .FirstOrDefaultAsync(pl => pl.TransactionId == transactionId);

        if (paymentLog == null)
        {
            return new PaymentResult { Success = false, ErrorMessage = "Payment not found" };
        }

        return new PaymentResult
        {
            Success = paymentLog.Status == "Completed",
            TransactionId = transactionId,
            Status = paymentLog.Status,
            Amount = paymentLog.Amount,
            Currency = paymentLog.Currency,
            ProcessedAt = paymentLog.UpdatedAt
        };
    }

    public async Task<List<PaymentLog>> GetPaymentLogsAsync(Guid accountId)
    {
        return await _context.PaymentLogs
            .Where(pl => pl.AccountId == accountId)
            .OrderByDescending(pl => pl.CreatedAt)
            .ToListAsync();
    }

    private async Task<GatewayResponse> SimulateGatewayProcessing(PaymentRequest request, string transactionId)
    {
        // Simulate network delay
        await Task.Delay(500);

        // Simulate success rate (95% success)
        var random = new Random();
        var isSuccess = random.Next(100) < 95;

        if (isSuccess)
        {
            return new GatewayResponse
            {
                Success = true,
                GatewayTransactionId = $"GW{Guid.NewGuid():N}",
                ProcessedAt = DateTime.UtcNow
            };
        }

        return new GatewayResponse
        {
            Success = false,
            ErrorMessage = "Payment declined by gateway"
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
    public string CardToken { get; set; } = string.Empty;
    public string BillingAddress { get; set; } = string.Empty;
    public string BillingZip { get; set; } = string.Empty;
}

public class PaymentResult
{
    public bool Success { get; set; }
    public string TransactionId { get; set; } = string.Empty;
    public string? GatewayTransactionId { get; set; }
    public decimal Amount { get; set; }
    public string Currency { get; set; } = "USD";
    public string? Status { get; set; }
    public DateTime? ProcessedAt { get; set; }
    public string? ErrorMessage { get; set; }
}

public class PaymentLog
{
    public Guid Id { get; set; }
    public Guid AccountId { get; set; }
    public string TransactionId { get; set; } = string.Empty;
    public string? GatewayTransactionId { get; set; }
    public decimal Amount { get; set; }
    public string Currency { get; set; } = "USD";
    public string MerchantName { get; set; } = string.Empty;
    public string Status { get; set; } = "Pending";
    public decimal? RefundAmount { get; set; }
    public string? ErrorMessage { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }

    // Navigation properties
    public Account Account { get; set; } = null!;
}

public class GatewayResponse
{
    public bool Success { get; set; }
    public string GatewayTransactionId { get; set; } = string.Empty;
    public DateTime ProcessedAt { get; set; }
    public string? ErrorMessage { get; set; }
}
