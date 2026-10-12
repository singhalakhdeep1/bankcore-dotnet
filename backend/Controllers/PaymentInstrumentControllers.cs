using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using BankingAPI.DTOs;
using BankingAPI.Extensions;
using BankingAPI.Models;
using BankingAPI.Services;

namespace BankingAPI.Controllers;

/// <summary>Tokenized cards and PayPal accounts. Card numbers are never stored.</summary>
[ApiController]
[Route("api/payment-methods")]
[Authorize]
public class PaymentMethodsController : ControllerBase
{
    private readonly IPaymentMethodService _methods;

    public PaymentMethodsController(IPaymentMethodService methods)
    {
        _methods = methods;
    }

    [HttpGet]
    public async Task<IActionResult> List()
    {
        if (User.GetUserId() is not { } userId) return Unauthorized();
        return Ok((await _methods.ListAsync(userId)).Select(PaymentMethodDto.From));
    }

    /// <summary>
    /// Tokenizes and stores a payment method. With live gateway keys send the client-side token in
    /// <c>providerToken</c>; in simulated mode raw test cards are accepted (4242424242424242 succeeds,
    /// 4000000000000002 is declined at charge time, 4000000000009995 has insufficient funds).
    /// </summary>
    [HttpPost]
    public async Task<IActionResult> Add([FromBody] AddPaymentMethodRequest request)
    {
        if (User.GetUserId() is not { } userId) return Unauthorized();

        var (method, error) = await _methods.AddAsync(userId, request);
        return method == null
            ? BadRequest(new { message = error })
            : StatusCode(StatusCodes.Status201Created, PaymentMethodDto.From(method));
    }

    [HttpPost("{id:guid}/default")]
    public async Task<IActionResult> MakeDefault(Guid id)
    {
        if (User.GetUserId() is not { } userId) return Unauthorized();
        return await _methods.SetDefaultAsync(userId, id) ? NoContent() : NotFound(new { message = "Payment method not found" });
    }

    [HttpDelete("{id:guid}")]
    public async Task<IActionResult> Remove(Guid id)
    {
        if (User.GetUserId() is not { } userId) return Unauthorized();
        return await _methods.RemoveAsync(userId, id) ? NoContent() : NotFound(new { message = "Payment method not found" });
    }
}

/// <summary>Standing instructions to pay a merchant daily, weekly or monthly.</summary>
[ApiController]
[Route("api/recurring-payments")]
[Authorize]
public class RecurringPaymentsController : ControllerBase
{
    private readonly IRecurringPaymentService _recurring;

    public RecurringPaymentsController(IRecurringPaymentService recurring)
    {
        _recurring = recurring;
    }

    [HttpGet]
    public async Task<IActionResult> List()
    {
        if (User.GetUserId() is not { } userId) return Unauthorized();
        return Ok((await _recurring.ListAsync(userId)).Select(RecurringPaymentDto.From));
    }

    [HttpPost]
    public async Task<IActionResult> Create([FromBody] CreateRecurringPaymentRequest request)
    {
        if (User.GetUserId() is not { } userId) return Unauthorized();

        var (payment, error) = await _recurring.CreateAsync(userId, request);
        return payment == null ? BadRequest(new { message = error }) : StatusCode(StatusCodes.Status201Created, RecurringPaymentDto.From(payment));
    }

    [HttpPost("{id:guid}/pause")]
    public Task<IActionResult> Pause(Guid id) => Set(id, RecurringStatus.Paused);

    [HttpPost("{id:guid}/resume")]
    public Task<IActionResult> Resume(Guid id) => Set(id, RecurringStatus.Active);

    [HttpDelete("{id:guid}")]
    public Task<IActionResult> Cancel(Guid id) => Set(id, RecurringStatus.Cancelled);

    private async Task<IActionResult> Set(Guid id, RecurringStatus status)
    {
        if (User.GetUserId() is not { } userId) return Unauthorized();
        return await _recurring.SetStatusAsync(userId, id, status) ? NoContent() : NotFound(new { message = "Recurring payment not found" });
    }
}

/// <summary>Outgoing ACH, wire and SWIFT transfers.</summary>
[ApiController]
[Route("api/bank-transfers")]
[Authorize]
public class BankTransfersController : ControllerBase
{
    private readonly IBankTransferService _transfers;
    private readonly IAccountService _accounts;

    public BankTransfersController(IBankTransferService transfers, IAccountService accounts)
    {
        _transfers = transfers;
        _accounts = accounts;
    }

    /// <summary>
    /// Sends money to an external bank. ACH needs an ABA routing number and account (fee 0.50, about 2 business days);
    /// wire the same (fee 15, same day); SWIFT needs a BIC plus an IBAN or account number (fee 35 + 0.1%, about 3 business days).
    /// </summary>
    [HttpPost]
    public async Task<IActionResult> Submit([FromBody] BankTransferRequest request)
    {
        if (User.GetUserId() is not { } userId) return Unauthorized();

        var (transfer, error) = await _transfers.SubmitAsync(userId, request);
        return transfer == null ? BadRequest(new { message = error }) : StatusCode(StatusCodes.Status201Created, BankTransferDto.From(transfer));
    }

    [HttpGet]
    public async Task<IActionResult> List([FromQuery] Guid accountId)
    {
        if (User.GetUserId() is not { } userId) return Unauthorized();
        if (await _accounts.GetAccountByIdAsync(accountId, userId) == null) return NotFound(new { message = "Account not found" });

        return Ok((await _transfers.ListAsync(accountId)).Select(BankTransferDto.From));
    }

    [HttpGet("{id:guid}")]
    public async Task<IActionResult> Get(Guid id)
    {
        if (User.GetUserId() is not { } userId) return Unauthorized();

        var transfer = await _transfers.GetAsync(id);
        if (transfer == null || await _accounts.GetAccountByIdAsync(transfer.AccountId, userId) == null)
        {
            return NotFound(new { message = "Transfer not found" });
        }

        return Ok(BankTransferDto.From(transfer));
    }

    /// <summary>Fee schedule, so clients can show the cost before submitting.</summary>
    [HttpGet("fees")]
    [AllowAnonymous]
    public IActionResult Fees([FromQuery] BankTransferRail rail, [FromQuery] decimal amount) =>
        Ok(new { rail = rail.ToString(), amount, fee = BankTransferService.CalculateFee(rail, amount) });
}
