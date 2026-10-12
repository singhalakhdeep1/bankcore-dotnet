using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using BankingAPI.DTOs;
using BankingAPI.Extensions;
using BankingAPI.Models;
using BankingAPI.Services;

namespace BankingAPI.Controllers;

/// <summary>Merchant payments, card top-ups through the payment gateway, refunds and disputes.</summary>
[ApiController]
[Route("api/[controller]")]
[Authorize]
public class PaymentsController : ControllerBase
{
    private readonly IPaymentGatewayService _paymentService;
    private readonly IAccountService _accountService;
    private readonly IChargebackService _chargebacks;

    public PaymentsController(IPaymentGatewayService paymentService, IAccountService accountService, IChargebackService chargebacks)
    {
        _paymentService = paymentService;
        _accountService = accountService;
        _chargebacks = chargebacks;
    }

    /// <summary>Pays a merchant from the account balance.</summary>
    [HttpPost]
    public async Task<IActionResult> CreatePayment([FromBody] PaymentRequestDto request)
    {
        if (User.GetUserId() is not { } userId) return Unauthorized();
        if (await _accountService.GetAccountByIdAsync(request.AccountId, userId) == null)
        {
            return NotFound(new { message = "Account not found" });
        }

        var result = await _paymentService.ProcessPaymentAsync(new PaymentRequest
        {
            AccountId = request.AccountId,
            UserId = userId,
            Amount = request.Amount,
            MerchantName = request.MerchantName.Trim(),
            Description = request.Description
        });

        return result.Success ? Ok(result) : BadRequest(result);
    }

    /// <summary>
    /// Adds money to an account by charging a stored payment method through Stripe or PayPal.
    /// The response says whether the gateway was live or simulated.
    /// </summary>
    [HttpPost("top-up")]
    public async Task<IActionResult> TopUp([FromBody] CardTopUpDto request)
    {
        if (User.GetUserId() is not { } userId) return Unauthorized();
        if (await _accountService.GetAccountByIdAsync(request.AccountId, userId) == null)
        {
            return NotFound(new { message = "Account not found" });
        }

        var result = await _paymentService.TopUpFromCardAsync(new CardTopUpRequest
        {
            AccountId = request.AccountId,
            UserId = userId,
            Amount = request.Amount,
            PaymentMethodId = request.PaymentMethodId,
            Description = request.Description
        });

        return result.Success ? Ok(result) : BadRequest(result);
    }

    [HttpGet]
    public async Task<IActionResult> GetPayments([FromQuery] Guid accountId)
    {
        if (User.GetUserId() is not { } userId) return Unauthorized();
        if (await _accountService.GetAccountByIdAsync(accountId, userId) == null)
        {
            return NotFound(new { message = "Account not found" });
        }

        var logs = await _paymentService.GetPaymentLogsAsync(accountId);
        return Ok(logs.Select(l => new
        {
            l.TransactionId,
            l.Kind,
            l.Provider,
            mode = l.ProviderMode,
            l.GatewayTransactionId,
            l.Amount,
            l.Currency,
            l.Fee,
            l.MerchantName,
            l.Status,
            l.RefundAmount,
            l.ErrorMessage,
            l.CreatedAt,
            l.UpdatedAt
        }));
    }

    [HttpGet("{transactionId}")]
    public async Task<IActionResult> GetPayment(string transactionId)
    {
        var payment = await GetOwnedPaymentAsync(transactionId);
        return payment == null ? NotFound(new { message = "Payment not found" }) : Ok(payment);
    }

    /// <summary>Refunds a payment. Merchant payments can be refunded partially; card top-ups only in full.</summary>
    [HttpPost("{transactionId}/refund")]
    public async Task<IActionResult> Refund(string transactionId, [FromBody] RefundRequestDto? request)
    {
        if (await GetOwnedPaymentAsync(transactionId) == null)
        {
            return NotFound(new { message = "Payment not found" });
        }

        var result = await _paymentService.RefundPaymentAsync(transactionId, request?.Amount);
        return result.Success ? Ok(result) : BadRequest(result);
    }

    /// <summary>Disputes a completed merchant payment (within 120 days). Staff review it and may credit the amount back.</summary>
    [HttpPost("{transactionId}/chargeback")]
    public async Task<IActionResult> OpenChargeback(string transactionId, [FromBody] ChargebackRequest request)
    {
        if (User.GetUserId() is not { } userId) return Unauthorized();

        var (chargeback, error) = await _chargebacks.OpenAsync(userId, transactionId, request.Reason);
        return chargeback == null
            ? BadRequest(new { message = error })
            : StatusCode(StatusCodes.Status201Created, ChargebackDto.From(chargeback, transactionId));
    }

    [HttpGet("chargebacks")]
    public async Task<IActionResult> MyChargebacks()
    {
        if (User.GetUserId() is not { } userId) return Unauthorized();

        var items = await _chargebacks.ListForUserAsync(userId);
        return Ok(items.Select(c => ChargebackDto.From(c, c.PaymentLog.TransactionId)));
    }

    /// <summary>Returns the payment only if it belongs to one of the caller's accounts.</summary>
    private async Task<PaymentResult?> GetOwnedPaymentAsync(string transactionId)
    {
        if (User.GetUserId() is not { } userId) return null;

        var payment = await _paymentService.GetPaymentStatusAsync(transactionId);
        if (payment.AccountId == Guid.Empty) return null;

        return await _accountService.GetAccountByIdAsync(payment.AccountId, userId) == null ? null : payment;
    }
}

public class ChargebackDto
{
    public Guid Id { get; set; }
    public string PaymentReference { get; set; } = string.Empty;
    public decimal Amount { get; set; }
    public string Reason { get; set; } = string.Empty;
    public string Status { get; set; } = string.Empty;
    public string? ResolutionNotes { get; set; }
    public DateTime OpenedAt { get; set; }
    public DateTime? ResolvedAt { get; set; }

    public static ChargebackDto From(Chargeback c, string paymentReference) => new()
    {
        Id = c.Id,
        PaymentReference = paymentReference,
        Amount = c.Amount,
        Reason = c.Reason,
        Status = c.Status.ToString(),
        ResolutionNotes = c.ResolutionNotes,
        OpenedAt = c.OpenedAt,
        ResolvedAt = c.ResolvedAt
    };
}
