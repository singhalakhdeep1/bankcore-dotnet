using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Authorization;
using BankingAPI.Services;
using BankingAPI.DTOs;
using BankingAPI.Extensions;

namespace BankingAPI.Controllers;

[ApiController]
[Route("api/[controller]")]
[Authorize]
public class PaymentsController : ControllerBase
{
    private readonly IPaymentGatewayService _paymentService;
    private readonly IAccountService _accountService;

    public PaymentsController(IPaymentGatewayService paymentService, IAccountService accountService)
    {
        _paymentService = paymentService;
        _accountService = accountService;
    }

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
            Amount = request.Amount,
            MerchantName = request.MerchantName.Trim(),
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
            l.GatewayTransactionId,
            l.Amount,
            l.Currency,
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

    /// <summary>Returns the payment only if it belongs to one of the caller's accounts.</summary>
    private async Task<PaymentResult?> GetOwnedPaymentAsync(string transactionId)
    {
        if (User.GetUserId() is not { } userId) return null;

        var payment = await _paymentService.GetPaymentStatusAsync(transactionId);
        if (payment.AccountId == Guid.Empty) return null;

        return await _accountService.GetAccountByIdAsync(payment.AccountId, userId) == null ? null : payment;
    }
}
