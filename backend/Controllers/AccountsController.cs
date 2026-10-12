using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Authorization;
using BankingAPI.Services;
using BankingAPI.DTOs;
using BankingAPI.Extensions;
using BankingAPI.Models;

namespace BankingAPI.Controllers;

[ApiController]
[Route("api/[controller]")]
[Authorize]
public class AccountsController : ControllerBase
{
    private readonly IAccountService _accountService;
    private readonly ITransactionService _transactionService;
    private readonly ILedgerService _ledgerService;
    private readonly IComplianceService _complianceService;

    public AccountsController(
        IAccountService accountService,
        ITransactionService transactionService,
        ILedgerService ledgerService,
        IComplianceService complianceService)
    {
        _accountService = accountService;
        _transactionService = transactionService;
        _ledgerService = ledgerService;
        _complianceService = complianceService;
    }

    [HttpGet]
    public async Task<IActionResult> GetAccounts()
    {
        if (User.GetUserId() is not { } userId) return Unauthorized();
        return Ok(await _accountService.GetUserAccountsAsync(userId));
    }

    [HttpGet("{id:guid}")]
    public async Task<IActionResult> GetAccount(Guid id)
    {
        if (User.GetUserId() is not { } userId) return Unauthorized();

        var account = await _accountService.GetAccountByIdAsync(id, userId);
        return account == null ? NotFound(new { message = "Account not found" }) : Ok(account);
    }

    [HttpGet("{id:guid}/transactions")]
    public async Task<IActionResult> GetTransactions(Guid id, [FromQuery] int page = 1, [FromQuery] int pageSize = 20)
    {
        if (User.GetUserId() is not { } userId) return Unauthorized();
        if (await _accountService.GetAccountByIdAsync(id, userId) == null)
        {
            return NotFound(new { message = "Account not found" });
        }

        page = Math.Max(1, page);
        pageSize = Math.Clamp(pageSize, 1, 100);

        var (transactions, totalCount) = await _transactionService.GetAccountTransactionsAsync(id, page, pageSize);
        return Ok(new PagedResult<TransactionDto>
        {
            Items = transactions.Select(TransactionDto.From).ToList(),
            Page = page,
            PageSize = pageSize,
            TotalCount = totalCount
        });
    }

    [HttpGet("{id:guid}/ledger")]
    public async Task<IActionResult> GetLedger(Guid id)
    {
        if (User.GetUserId() is not { } userId) return Unauthorized();
        if (await _accountService.GetAccountByIdAsync(id, userId) == null)
        {
            return NotFound(new { message = "Account not found" });
        }

        var entries = await _ledgerService.GetAccountLedgerAsync(id);
        var reconciled = await _ledgerService.ReconcileAccountAsync(id);

        return Ok(new
        {
            reconciled,
            entries = entries.Select(e => new
            {
                e.Id,
                e.TransactionId,
                e.Debit,
                e.Credit,
                e.BalanceAfter,
                e.Description,
                e.CreatedAt
            })
        });
    }

    [HttpGet("{id:guid}/compliance")]
    public async Task<IActionResult> GetComplianceReport(Guid id, [FromQuery] int days = 30)
    {
        if (User.GetUserId() is not { } userId) return Unauthorized();
        if (await _accountService.GetAccountByIdAsync(id, userId) == null)
        {
            return NotFound(new { message = "Account not found" });
        }

        days = Math.Clamp(days, 1, 365);
        var end = DateTime.UtcNow;
        var report = await _complianceService.GenerateComplianceReportAsync(id, end.AddDays(-days), end);
        return Ok(report);
    }

    [HttpPost]
    public async Task<IActionResult> CreateAccount([FromBody] CreateAccountRequest request)
    {
        if (User.GetUserId() is not { } userId) return Unauthorized();

        var account = await _accountService.CreateAccountAsync(userId, request);
        if (account == null)
        {
            return BadRequest(new { message = $"Invalid account type. Use one of: {string.Join(", ", Enum.GetNames<AccountType>())}" });
        }

        return CreatedAtAction(nameof(GetAccount), new { id = account.Id }, account);
    }

    [HttpPost("{id:guid}/deposit")]
    public async Task<IActionResult> Deposit(Guid id, [FromBody] DepositRequest request)
    {
        if (User.GetUserId() is not { } userId) return Unauthorized();
        if (await _accountService.GetAccountByIdAsync(id, userId) == null)
        {
            return NotFound(new { message = "Account not found" });
        }

        try
        {
            var transaction = await _transactionService.DepositAsync(id, request.Amount, request.Description);
            return Ok(TransactionDto.From(transaction!));
        }
        catch (InvalidOperationException ex)
        {
            return BadRequest(new { message = ex.Message });
        }
    }

    [HttpPost("{id:guid}/withdraw")]
    public async Task<IActionResult> Withdraw(Guid id, [FromBody] WithdrawRequest request)
    {
        if (User.GetUserId() is not { } userId) return Unauthorized();
        if (await _accountService.GetAccountByIdAsync(id, userId) == null)
        {
            return NotFound(new { message = "Account not found" });
        }

        try
        {
            var transaction = await _transactionService.WithdrawAsync(id, request.Amount, request.Description);
            return Ok(TransactionDto.From(transaction!));
        }
        catch (InvalidOperationException ex)
        {
            return BadRequest(new { message = ex.Message });
        }
    }

    /// <summary>Closes an account (soft delete). The balance must be zero.</summary>
    [HttpDelete("{id:guid}")]
    public async Task<IActionResult> Close(Guid id)
    {
        if (User.GetUserId() is not { } userId) return Unauthorized();

        var error = await _accountService.CloseAccountAsync(id, userId);
        return error == null ? NoContent() : BadRequest(new { message = error });
    }

    [HttpPost("transfer")]
    public async Task<IActionResult> Transfer([FromBody] TransferRequest request)
    {
        if (User.GetUserId() is not { } userId) return Unauthorized();

        var toAccountId = request.ToAccountId;
        if (toAccountId == null && !string.IsNullOrWhiteSpace(request.ToAccountNumber))
        {
            toAccountId = await _accountService.GetAccountIdByNumberAsync(request.ToAccountNumber);
        }

        if (toAccountId == null)
        {
            return NotFound(new { message = "Destination account not found" });
        }

        if (request.FromAccountId == toAccountId)
        {
            return BadRequest(new { message = "Cannot transfer to the same account" });
        }

        // The source account must belong to the caller; the destination may be anyone's.
        if (await _accountService.GetAccountByIdAsync(request.FromAccountId, userId) == null)
        {
            return NotFound(new { message = "Source account not found" });
        }

        // Scope the key to the caller so one customer's key can never collide with another's.
        var idempotencyKey = string.IsNullOrWhiteSpace(request.IdempotencyKey)
            ? null
            : $"{userId:N}-{request.IdempotencyKey.Trim()}";

        try
        {
            var (fromTx, toTx) = await _transactionService.TransferAsync(
                request.FromAccountId, toAccountId.Value, request.Amount, request.Description, idempotencyKey);

            return Ok(new TransferResultDto
            {
                From = TransactionDto.From(fromTx!),
                To = TransactionDto.From(toTx!)
            });
        }
        catch (InvalidOperationException ex)
        {
            return BadRequest(new { message = ex.Message });
        }
    }
}
