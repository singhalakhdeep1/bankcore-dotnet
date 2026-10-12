using System.ComponentModel.DataAnnotations;

namespace BankingAPI.DTOs;

public class RegisterRequest
{
    [Required, EmailAddress, MaxLength(255)]
    public string Email { get; set; } = string.Empty;

    /// <summary>At least 10 characters with upper case, lower case, a digit and a symbol.</summary>
    [Required, MaxLength(128)]
    public string Password { get; set; } = string.Empty;

    [Required, MaxLength(100)]
    public string FirstName { get; set; } = string.Empty;

    [Required, MaxLength(100)]
    public string LastName { get; set; } = string.Empty;

    [Phone, MaxLength(30)]
    public string? Phone { get; set; }
}

public class LoginRequest
{
    [Required, EmailAddress]
    public string Email { get; set; } = string.Empty;

    [Required]
    public string Password { get; set; } = string.Empty;

    /// <summary>6-digit authenticator code. Required when two-factor authentication is enabled.</summary>
    [StringLength(8, MinimumLength = 6)]
    public string? TotpCode { get; set; }
}

public class RefreshRequest
{
    [Required]
    public string RefreshToken { get; set; } = string.Empty;
}

public class LogoutRequest
{
    /// <summary>The refresh token to revoke along with the current access token.</summary>
    public string? RefreshToken { get; set; }
}

public class AuthResponse
{
    public string AccessToken { get; set; } = string.Empty;
    public string RefreshToken { get; set; } = string.Empty;
    public string TokenType { get; set; } = "Bearer";
    public UserDto User { get; set; } = null!;
}

public class UserDto
{
    public Guid Id { get; set; }
    public string Email { get; set; } = string.Empty;
    public string FirstName { get; set; } = string.Empty;
    public string LastName { get; set; } = string.Empty;
    public string? Phone { get; set; }
    public string? Address { get; set; }
    public string? City { get; set; }
    public string? State { get; set; }
    public string? Country { get; set; }
    public string? PostalCode { get; set; }
    public string Role { get; set; } = string.Empty;
    public string KycStatus { get; set; } = string.Empty;
    public bool IsActive { get; set; }
    public bool TwoFactorEnabled { get; set; }
    public DateTime CreatedAt { get; set; }
}

public enum LoginOutcome
{
    Success,
    InvalidCredentials,
    LockedOut,
    AccountDisabled,
    TwoFactorRequired,
    InvalidTwoFactorCode
}

/// <summary>Result of a login attempt, so the API can tell a locked-out user from a wrong password.</summary>
public class LoginResult
{
    public LoginOutcome Outcome { get; set; }
    public AuthResponse? Auth { get; set; }
    public DateTime? LockedUntil { get; set; }
}

public class TwoFactorSetupResponse
{
    /// <summary>Base32 secret to type into an authenticator app.</summary>
    public string Secret { get; set; } = string.Empty;

    /// <summary>otpauth:// URI that authenticator apps can import (render it as a QR code).</summary>
    public string OtpAuthUri { get; set; } = string.Empty;
}

public class EnableTwoFactorRequest
{
    [Required, StringLength(8, MinimumLength = 6)]
    public string Code { get; set; } = string.Empty;
}

public class DisableTwoFactorRequest
{
    [Required]
    public string Password { get; set; } = string.Empty;

    [Required, StringLength(8, MinimumLength = 6)]
    public string Code { get; set; } = string.Empty;
}

public class ChangePasswordRequest
{
    [Required]
    public string CurrentPassword { get; set; } = string.Empty;

    [Required, MaxLength(128)]
    public string NewPassword { get; set; } = string.Empty;
}

public class UpdateProfileRequest
{
    [Required, MaxLength(100)]
    public string FirstName { get; set; } = string.Empty;

    [Required, MaxLength(100)]
    public string LastName { get; set; } = string.Empty;

    [Phone, MaxLength(30)]
    public string? Phone { get; set; }

    [MaxLength(200)]
    public string? Address { get; set; }

    [MaxLength(100)]
    public string? City { get; set; }

    [MaxLength(100)]
    public string? State { get; set; }

    [MaxLength(100)]
    public string? Country { get; set; }

    [MaxLength(20)]
    public string? PostalCode { get; set; }
}
