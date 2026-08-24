using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Authorization;
using BankingAPI.Services;
using BankingAPI.DTOs;
using System.Security.Claims;

namespace BankingAPI.Controllers;

[ApiController]
[Route("api/[controller]")]
[Authorize]
public class AccountsController : ControllerBase
{
    private readonly IAccountService _accountService;
    private readonly ITransactionService _transactionService;

    public AccountsController(IAccountService accountService, ITransactionService transactionService)
    {
        _accountService = accountService;
        _transactionService = transactionService;
    }

    [HttpGet]
    public async Task<IActionResult> GetAccounts()
    {
        var userId = GetUserId();
        var accounts = await _accountService.GetUserAccountsAsync(userId);
        return Ok(accounts);
    }

    [HttpGet("{id}")]
    public async Task<IActionResult> GetAccount(Guid id)
    {
        var userId = GetUserId();
        var account = await _accountService.GetAccountByIdAsync(id, userId);
        
        if (account == null)
        {
            return NotFound(new { message = "Account not found" });
        }

        return Ok(account);
    }

    [HttpGet("{id}/transactions")]
    public async Task<IActionResult> GetTransactions(Guid id, [FromQuery] int page = 1, [FromQuery] int pageSize = 20)
    {
        var userId = GetUserId();
        var account = await _accountService.GetAccountByIdAsync(id, userId);
        
        if (account == null)
        {
            return NotFound(new { message = "Account not found" });
        }

        var transactions = await _transactionService.GetAccountTransactionsAsync(id, page, pageSize);
        return Ok(transactions);
    }

    [HttpPost]
    public async Task<IActionResult> CreateAccount([FromBody] CreateAccountRequest request)
    {
        var userId = GetUserId();
        var account = await _accountService.CreateAccountAsync(userId, request);
        
        if (account == null)
        {
            return BadRequest(new { message = "Invalid account type" });
        }

        return CreatedAtAction(nameof(GetAccount), new { id = account.Id }, account);
    }

    [HttpPost("{id}/deposit")]
    public async Task<IActionResult> Deposit(Guid id, [FromBody] DepositRequest request)
    {
        var userId = GetUserId();
        var account = await _accountService.GetAccountByIdAsync(id, userId);
        
        if (account == null)
        {
            return NotFound(new { message = "Account not found" });
        }

        if (account.Status != Models.AccountStatus.Active)
        {
            return BadRequest(new { message = "Account is not active" });
        }

        if (request.Amount <= 0)
        {
            return BadRequest(new { message = "Amount must be positive" });
        }

        try
        {
            var transaction = await _transactionService.DepositAsync(id, request.Amount, request.Description);
            return Ok(transaction);
        }
        catch (Exception ex)
        {
            return BadRequest(new { message = ex.Message });
        }
    }

    [HttpPost("{id}/withdraw")]
    public async Task<IActionResult> Withdraw(Guid id, [FromBody] WithdrawRequest request)
    {
        var userId = GetUserId();
        var account = await _accountService.GetAccountByIdAsync(id, userId);
        
        if (account == null)
        {
            return NotFound(new { message = "Account not found" });
        }

        if (account.Status != Models.AccountStatus.Active)
        {
            return BadRequest(new { message = "Account is not active" });
        }

        if (request.Amount <= 0)
        {
            return BadRequest(new { message = "Amount must be positive" });
        }

        try
        {
            var transaction = await _transactionService.WithdrawAsync(id, request.Amount, request.Description);
            return Ok(transaction);
        }
        catch (Exception ex)
        {
            return BadRequest(new { message = ex.Message });
        }
    }

    [HttpPost("transfer")]
    public async Task<IActionResult> Transfer([FromBody] TransferRequest request)
    {
        var userId = GetUserId();
        
        if (request.Amount <= 0)
        {
            return BadRequest(new { message = "Amount must be positive" });
        }

        if (request.FromAccountId == request.ToAccountId)
        {
            return BadRequest(new { message = "Cannot transfer to same account" });
        }

        try
        {
            var transactions = await _transactionService.TransferAsync(
                request.FromAccountId, 
                request.ToAccountId, 
                request.Amount, 
                request.Description,
                request.IdempotencyKey
            );
            return Ok(transactions);
        }
        catch (Exception ex)
        {
            return BadRequest(new { message = ex.Message });
        }
    }

    private Guid GetUserId()
    {
        var userIdClaim = User.FindFirst(ClaimTypes.NameIdentifier)?.Value;
        return Guid.Parse(userIdClaim!);
    }
}
