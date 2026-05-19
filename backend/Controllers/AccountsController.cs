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

    public AccountsController(IAccountService accountService)
    {
        _accountService = accountService;
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

    private Guid GetUserId()
    {
        var userIdClaim = User.FindFirst(ClaimTypes.NameIdentifier)?.Value;
        return Guid.Parse(userIdClaim!);
    }
}
