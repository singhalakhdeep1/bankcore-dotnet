using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using BankingAPI.DTOs;
using BankingAPI.Extensions;
using BankingAPI.Services;

namespace BankingAPI.Controllers;

/// <summary>Registration, login (with optional two-factor), token refresh and logout.</summary>
[ApiController]
[Route("api/[controller]")]
public class AuthController : ControllerBase
{
    private readonly IAuthService _authService;

    public AuthController(IAuthService authService)
    {
        _authService = authService;
    }

    /// <summary>Creates a customer account. The password must be at least 10 characters with upper case, lower case, a digit and a symbol.</summary>
    /// <response code="201">Registered; returns access and refresh tokens.</response>
    /// <response code="400">Validation failed or the password is too weak.</response>
    /// <response code="409">The email is already registered.</response>
    [HttpPost("register")]
    [EnableRateLimiting("auth")]
    [ProducesResponseType(typeof(AuthResponse), StatusCodes.Status201Created)]
    public async Task<IActionResult> Register([FromBody] RegisterRequest request)
    {
        var result = await _authService.RegisterWithResultAsync(request);

        return result.Outcome switch
        {
            RegisterOutcome.Success => StatusCode(StatusCodes.Status201Created, result.Auth),
            RegisterOutcome.EmailTaken => Conflict(new { message = "Email already registered" }),
            RegisterOutcome.WeakPassword => BadRequest(new { message = "The password does not meet the policy.", problems = result.Problems }),
            _ => StatusCode(StatusCodes.Status403Forbidden, new { message = "We are unable to open an account for you." })
        };
    }

    /// <summary>
    /// Logs in. When two-factor authentication is on and no <c>totpCode</c> is sent the response is 401 with
    /// <c>requiresTwoFactor: true</c>. After 5 failed attempts the account is locked for 15 minutes (423).
    /// </summary>
    [HttpPost("login")]
    [EnableRateLimiting("auth")]
    [ProducesResponseType(typeof(AuthResponse), StatusCodes.Status200OK)]
    public async Task<IActionResult> Login([FromBody] LoginRequest request)
    {
        var result = await _authService.AuthenticateAsync(request);

        return result.Outcome switch
        {
            LoginOutcome.Success => Ok(result.Auth),
            LoginOutcome.TwoFactorRequired => Unauthorized(new { message = "Enter the 6-digit code from your authenticator app.", requiresTwoFactor = true }),
            LoginOutcome.InvalidTwoFactorCode => Unauthorized(new { message = "That code is not valid.", requiresTwoFactor = true }),
            LoginOutcome.LockedOut => StatusCode(StatusCodes.Status423Locked, new
            {
                message = result.LockedUntil.HasValue
                    ? $"Too many failed attempts. Try again after {result.LockedUntil.Value:HH:mm} UTC."
                    : "This account is locked. Contact support.",
                lockedUntil = result.LockedUntil
            }),
            LoginOutcome.AccountDisabled => StatusCode(StatusCodes.Status403Forbidden, new { message = "This account is disabled. Contact support." }),
            _ => Unauthorized(new { message = "Invalid credentials" })
        };
    }

    /// <summary>Exchanges a refresh token for a new token pair. The old refresh token stops working.</summary>
    [HttpPost("refresh")]
    [EnableRateLimiting("auth")]
    public async Task<IActionResult> Refresh([FromBody] RefreshRequest request)
    {
        var result = await _authService.RefreshAsync(request.RefreshToken);
        return result == null ? Unauthorized(new { message = "Invalid or expired refresh token" }) : Ok(result);
    }

    /// <summary>Revokes the current access token and, if sent, the refresh token.</summary>
    [HttpPost("logout")]
    [Authorize]
    public async Task<IActionResult> Logout([FromBody] LogoutRequest? request)
    {
        var jti = User.FindFirst("jti")?.Value;
        DateTime? expires = long.TryParse(User.FindFirst("exp")?.Value, out var exp)
            ? DateTimeOffset.FromUnixTimeSeconds(exp).UtcDateTime
            : null;

        await _authService.LogoutAsync(jti, expires, request?.RefreshToken);
        return Ok(new { message = "Logged out successfully" });
    }

    /// <summary>Step 1 of enabling two-factor: returns a secret and an otpauth:// URI for an authenticator app.</summary>
    [HttpPost("2fa/setup")]
    [Authorize]
    public async Task<IActionResult> TwoFactorSetup()
    {
        if (User.GetUserId() is not { } userId) return Unauthorized();

        var setup = await _authService.BeginTwoFactorSetupAsync(userId);
        return setup == null ? BadRequest(new { message = "Two-factor authentication is already enabled." }) : Ok(setup);
    }

    /// <summary>Step 2: confirm with a code from the app to turn two-factor on.</summary>
    [HttpPost("2fa/enable")]
    [Authorize]
    public async Task<IActionResult> TwoFactorEnable([FromBody] EnableTwoFactorRequest request)
    {
        if (User.GetUserId() is not { } userId) return Unauthorized();

        return await _authService.EnableTwoFactorAsync(userId, request.Code)
            ? Ok(new { message = "Two-factor authentication is on." })
            : BadRequest(new { message = "The code is not valid. Start again from setup if the secret was lost." });
    }

    /// <summary>Turns two-factor off; needs the password and a current code.</summary>
    [HttpPost("2fa/disable")]
    [Authorize]
    public async Task<IActionResult> TwoFactorDisable([FromBody] DisableTwoFactorRequest request)
    {
        if (User.GetUserId() is not { } userId) return Unauthorized();

        return await _authService.DisableTwoFactorAsync(userId, request.Password, request.Code)
            ? Ok(new { message = "Two-factor authentication is off." })
            : BadRequest(new { message = "The password or code is not valid." });
    }
}
