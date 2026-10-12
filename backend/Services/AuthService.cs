using BankingAPI.Data;
using BankingAPI.Models;
using BankingAPI.DTOs;
using BankingAPI.Services.Security;
using Microsoft.EntityFrameworkCore;

namespace BankingAPI.Services;

public enum RegisterOutcome
{
    Success,
    EmailTaken,
    WeakPassword,
    Blocked
}

public class RegisterResult
{
    public RegisterOutcome Outcome { get; set; }
    public AuthResponse? Auth { get; set; }
    public List<string> Problems { get; set; } = new();
}

public interface IAuthService
{
    Task<AuthResponse?> RegisterAsync(RegisterRequest request);
    Task<RegisterResult> RegisterWithResultAsync(RegisterRequest request);
    Task<AuthResponse?> LoginAsync(LoginRequest request);
    Task<LoginResult> AuthenticateAsync(LoginRequest request);
    Task<AuthResponse?> RefreshAsync(string refreshToken);
    Task LogoutAsync(string? accessJti, DateTime? accessExpiresUtc, string? refreshToken);
    Task<UserDto?> GetUserByIdAsync(Guid userId);
    Task<UserDto?> UpdateProfileAsync(Guid userId, UpdateProfileRequest request);
    Task<List<string>> ChangePasswordAsync(Guid userId, ChangePasswordRequest request);
    Task<TwoFactorSetupResponse?> BeginTwoFactorSetupAsync(Guid userId);
    Task<bool> EnableTwoFactorAsync(Guid userId, string code);
    Task<bool> DisableTwoFactorAsync(Guid userId, string password, string code);
    Task<string?> DeleteAccountAsync(Guid userId, string password);
}

public class AuthService : IAuthService
{
    private readonly ApplicationDbContext _context;
    private readonly IJwtService _jwtService;
    private readonly ITokenStore _tokenStore;
    private readonly ITwoFactorService _twoFactor;
    private readonly ISanctionsService _sanctions;
    private readonly IAuditService _audit;
    private readonly IConfiguration _configuration;

    // Verified against when the email is unknown, so unknown and known users take the same time to reject.
    private static readonly string DummyHash = BCrypt.Net.BCrypt.HashPassword("dummy-password-for-timing");

    public AuthService(
        ApplicationDbContext context,
        IJwtService jwtService,
        ITokenStore tokenStore,
        ITwoFactorService twoFactor,
        ISanctionsService sanctions,
        IAuditService audit,
        IConfiguration configuration)
    {
        _context = context;
        _jwtService = jwtService;
        _tokenStore = tokenStore;
        _twoFactor = twoFactor;
        _sanctions = sanctions;
        _audit = audit;
        _configuration = configuration;
    }

    private int MaxFailedLogins => _configuration.GetValue("Security:MaxFailedLogins", 5);
    private int LockoutMinutes => _configuration.GetValue("Security:LockoutMinutes", 15);

    // ------------------------------------------------------------------ registration

    public async Task<AuthResponse?> RegisterAsync(RegisterRequest request) =>
        (await RegisterWithResultAsync(request)).Auth;

    public async Task<RegisterResult> RegisterWithResultAsync(RegisterRequest request)
    {
        var email = NormalizeEmail(request.Email);

        var problems = PasswordPolicy.Validate(request.Password, email);
        if (problems.Count > 0)
        {
            return new RegisterResult { Outcome = RegisterOutcome.WeakPassword, Problems = problems };
        }

        if (await _context.Users.IgnoreQueryFilters().AnyAsync(u => u.Email == email))
        {
            return new RegisterResult { Outcome = RegisterOutcome.EmailTaken };
        }

        var fullName = $"{request.FirstName} {request.LastName}";
        var hit = await _sanctions.ScreenAsync(fullName);
        if (hit != null)
        {
            await _audit.LogAsync("Register.SanctionsMatch", null, "SanctionedEntity", hit.Id.ToString(),
                $"Registration refused for '{fullName}' (matched {hit.ListName}: {hit.Name})");
            return new RegisterResult { Outcome = RegisterOutcome.Blocked };
        }

        var user = new User
        {
            Id = Guid.NewGuid(),
            Email = email,
            PasswordHash = BCrypt.Net.BCrypt.HashPassword(request.Password),
            FirstName = request.FirstName.Trim(),
            LastName = request.LastName.Trim(),
            Phone = request.Phone,
            Role = UserRole.Customer,
            KycStatus = KycStatus.Pending,
            IsActive = true
        };

        _context.Users.Add(user);
        try
        {
            await _context.SaveChangesAsync();
        }
        catch (DbUpdateException)
        {
            // Two registrations for the same email raced past the check above; the unique index caught it.
            _context.Entry(user).State = EntityState.Detached;
            return new RegisterResult { Outcome = RegisterOutcome.EmailTaken };
        }

        await _audit.LogAsync("Register", user.Id, "User", user.Id.ToString());
        return new RegisterResult { Outcome = RegisterOutcome.Success, Auth = BuildAuthResponse(user) };
    }

    // ------------------------------------------------------------------ login

    public async Task<AuthResponse?> LoginAsync(LoginRequest request) =>
        (await AuthenticateAsync(request)).Auth;

    public async Task<LoginResult> AuthenticateAsync(LoginRequest request)
    {
        var email = NormalizeEmail(request.Email);
        var user = await _context.Users.FirstOrDefaultAsync(u => u.Email == email);

        if (user == null)
        {
            BCrypt.Net.BCrypt.Verify(request.Password, DummyHash);
            await _audit.LogAsync("Login.Failed", null, "User", null, $"Unknown email {email}");
            return new LoginResult { Outcome = LoginOutcome.InvalidCredentials };
        }

        if (user.LockoutEnd.HasValue && user.LockoutEnd.Value > DateTime.UtcNow)
        {
            await _audit.LogAsync("Login.RejectedLockedOut", user.Id, "User", user.Id.ToString());
            return new LoginResult { Outcome = LoginOutcome.LockedOut, LockedUntil = user.LockoutEnd };
        }

        if (!user.IsActive || user.IsLocked)
        {
            await _audit.LogAsync("Login.RejectedDisabled", user.Id, "User", user.Id.ToString());
            return new LoginResult { Outcome = LoginOutcome.AccountDisabled };
        }

        if (!BCrypt.Net.BCrypt.Verify(request.Password, user.PasswordHash))
        {
            return await RecordFailureAsync(user, LoginOutcome.InvalidCredentials);
        }

        if (user.TwoFactorEnabled)
        {
            if (string.IsNullOrWhiteSpace(request.TotpCode))
            {
                return new LoginResult { Outcome = LoginOutcome.TwoFactorRequired };
            }

            if (!_twoFactor.VerifyCode(user.TwoFactorSecret ?? string.Empty, request.TotpCode))
            {
                return await RecordFailureAsync(user, LoginOutcome.InvalidTwoFactorCode);
            }
        }

        user.FailedLoginAttempts = 0;
        user.LockoutEnd = null;
        user.LastLoginAt = DateTime.UtcNow;
        await _context.SaveChangesAsync();

        await _audit.LogAsync("Login.Success", user.Id, "User", user.Id.ToString());
        return new LoginResult { Outcome = LoginOutcome.Success, Auth = BuildAuthResponse(user) };
    }

    private async Task<LoginResult> RecordFailureAsync(User user, LoginOutcome failure)
    {
        user.FailedLoginAttempts++;

        if (user.FailedLoginAttempts >= MaxFailedLogins)
        {
            user.LockoutEnd = DateTime.UtcNow.AddMinutes(LockoutMinutes);
            user.FailedLoginAttempts = 0;
            await _context.SaveChangesAsync();
            await _audit.LogAsync("Login.LockedOut", user.Id, "User", user.Id.ToString(),
                $"Locked for {LockoutMinutes} minutes after {MaxFailedLogins} failed attempts");
            return new LoginResult { Outcome = LoginOutcome.LockedOut, LockedUntil = user.LockoutEnd };
        }

        await _context.SaveChangesAsync();
        await _audit.LogAsync("Login.Failed", user.Id, "User", user.Id.ToString(), failure.ToString());
        return new LoginResult { Outcome = failure };
    }

    // ------------------------------------------------------------------ tokens

    /// <summary>Exchanges a valid refresh token for a new pair. The old refresh token is revoked (rotation).</summary>
    public async Task<AuthResponse?> RefreshAsync(string refreshToken)
    {
        var info = _jwtService.ValidateRefreshToken(refreshToken);
        if (info == null || await _tokenStore.IsRevokedAsync(info.Jti, failOpen: false)) return null;

        var user = await _context.Users.FindAsync(info.UserId);
        if (user == null || !user.IsActive || user.IsLocked) return null;
        if (user.LockoutEnd.HasValue && user.LockoutEnd.Value > DateTime.UtcNow) return null;

        await _tokenStore.RevokeAsync(info.Jti, info.ExpiresUtc);
        return BuildAuthResponse(user);
    }

    public async Task LogoutAsync(string? accessJti, DateTime? accessExpiresUtc, string? refreshToken)
    {
        if (!string.IsNullOrEmpty(accessJti) && accessExpiresUtc.HasValue)
        {
            await _tokenStore.RevokeAsync(accessJti, accessExpiresUtc.Value);
        }

        if (!string.IsNullOrWhiteSpace(refreshToken))
        {
            var info = _jwtService.ValidateRefreshToken(refreshToken);
            if (info != null)
            {
                await _tokenStore.RevokeAsync(info.Jti, info.ExpiresUtc);
            }
        }
    }

    // ------------------------------------------------------------------ profile

    public async Task<UserDto?> GetUserByIdAsync(Guid userId)
    {
        var user = await _context.Users.FindAsync(userId);
        return user != null ? MapToUserDto(user) : null;
    }

    public async Task<UserDto?> UpdateProfileAsync(Guid userId, UpdateProfileRequest request)
    {
        var user = await _context.Users.FindAsync(userId);
        if (user == null) return null;

        user.FirstName = request.FirstName.Trim();
        user.LastName = request.LastName.Trim();
        user.Phone = request.Phone;
        user.Address = request.Address;
        user.City = request.City;
        user.State = request.State;
        user.Country = request.Country;
        user.PostalCode = request.PostalCode;
        user.UpdatedAt = DateTime.UtcNow;

        await _context.SaveChangesAsync();
        await _audit.LogAsync("Profile.Updated", userId, "User", userId.ToString());
        return MapToUserDto(user);
    }

    /// <summary>Returns the policy problems (empty on success); a single "Current password is incorrect." for a wrong password.</summary>
    public async Task<List<string>> ChangePasswordAsync(Guid userId, ChangePasswordRequest request)
    {
        var user = await _context.Users.FindAsync(userId);
        if (user == null || !BCrypt.Net.BCrypt.Verify(request.CurrentPassword, user.PasswordHash))
        {
            return new List<string> { "Current password is incorrect." };
        }

        var problems = PasswordPolicy.Validate(request.NewPassword, user.Email);
        if (problems.Count > 0) return problems;

        user.PasswordHash = BCrypt.Net.BCrypt.HashPassword(request.NewPassword);
        user.UpdatedAt = DateTime.UtcNow;
        await _context.SaveChangesAsync();
        await _audit.LogAsync("Password.Changed", userId, "User", userId.ToString());
        return new List<string>();
    }

    // ------------------------------------------------------------------ two-factor

    public async Task<TwoFactorSetupResponse?> BeginTwoFactorSetupAsync(Guid userId)
    {
        var user = await _context.Users.FindAsync(userId);
        if (user == null || user.TwoFactorEnabled) return null;

        user.TwoFactorSecret = _twoFactor.GenerateSecret();
        await _context.SaveChangesAsync();

        return new TwoFactorSetupResponse
        {
            Secret = user.TwoFactorSecret,
            OtpAuthUri = _twoFactor.BuildOtpAuthUri(user.Email, user.TwoFactorSecret)
        };
    }

    public async Task<bool> EnableTwoFactorAsync(Guid userId, string code)
    {
        var user = await _context.Users.FindAsync(userId);
        if (user == null || user.TwoFactorEnabled || string.IsNullOrEmpty(user.TwoFactorSecret)) return false;
        if (!_twoFactor.VerifyCode(user.TwoFactorSecret, code)) return false;

        user.TwoFactorEnabled = true;
        await _context.SaveChangesAsync();
        await _audit.LogAsync("TwoFactor.Enabled", userId, "User", userId.ToString());
        return true;
    }

    public async Task<bool> DisableTwoFactorAsync(Guid userId, string password, string code)
    {
        var user = await _context.Users.FindAsync(userId);
        if (user == null || !user.TwoFactorEnabled) return false;
        if (!BCrypt.Net.BCrypt.Verify(password, user.PasswordHash)) return false;
        if (!_twoFactor.VerifyCode(user.TwoFactorSecret ?? string.Empty, code)) return false;

        user.TwoFactorEnabled = false;
        user.TwoFactorSecret = null;
        await _context.SaveChangesAsync();
        await _audit.LogAsync("TwoFactor.Disabled", userId, "User", userId.ToString());
        return true;
    }

    // ------------------------------------------------------------------ account closure (soft delete)

    /// <summary>Soft-deletes the user and their accounts. Returns an error message, or null on success.</summary>
    public async Task<string?> DeleteAccountAsync(Guid userId, string password)
    {
        var user = await _context.Users.FindAsync(userId);
        if (user == null || !BCrypt.Net.BCrypt.Verify(password, user.PasswordHash))
        {
            return "Password is incorrect.";
        }

        var accounts = await _context.Accounts.Where(a => a.UserId == userId).ToListAsync();
        if (accounts.Any(a => a.Balance != 0))
        {
            return "All accounts must have a zero balance before the profile can be closed.";
        }

        var hasPendingTransfers = await _context.BankTransfers
            .AnyAsync(t => accounts.Select(a => a.Id).Contains(t.AccountId)
                           && (t.Status == BankTransferStatus.Submitted || t.Status == BankTransferStatus.Processing));
        if (hasPendingTransfers)
        {
            return "Wait for pending bank transfers to settle before closing the profile.";
        }

        var now = DateTime.UtcNow;
        foreach (var account in accounts)
        {
            account.IsDeleted = true;
            account.DeletedAt = now;
            account.Status = AccountStatus.Closed;
        }

        user.IsDeleted = true;
        user.DeletedAt = now;
        user.IsActive = false;
        await _context.SaveChangesAsync();
        await _audit.LogAsync("User.Closed", userId, "User", userId.ToString());
        return null;
    }

    // ------------------------------------------------------------------ helpers

    private AuthResponse BuildAuthResponse(User user)
    {
        return new AuthResponse
        {
            AccessToken = _jwtService.GenerateAccessToken(user.Id, user.Email, user.Role.ToString()),
            RefreshToken = _jwtService.GenerateRefreshToken(user.Id),
            User = MapToUserDto(user)
        };
    }

    private static string NormalizeEmail(string email) => email.Trim().ToLowerInvariant();

    public static UserDto MapToUserDto(User user)
    {
        return new UserDto
        {
            Id = user.Id,
            Email = user.Email,
            FirstName = user.FirstName,
            LastName = user.LastName,
            Phone = user.Phone,
            Address = user.Address,
            City = user.City,
            State = user.State,
            Country = user.Country,
            PostalCode = user.PostalCode,
            Role = user.Role.ToString(),
            KycStatus = user.KycStatus.ToString(),
            IsActive = user.IsActive,
            TwoFactorEnabled = user.TwoFactorEnabled,
            CreatedAt = user.CreatedAt
        };
    }
}
