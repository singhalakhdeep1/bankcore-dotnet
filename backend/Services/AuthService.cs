using BankingAPI.Data;
using BankingAPI.Models;
using BankingAPI.DTOs;
using Microsoft.EntityFrameworkCore;
using BCrypt.Net;

namespace BankingAPI.Services;

public interface IAuthService
{
    Task<AuthResponse?> RegisterAsync(RegisterRequest request);
    Task<AuthResponse?> LoginAsync(LoginRequest request);
    Task<AuthResponse?> RefreshAsync(string refreshToken);
    Task<UserDto?> GetUserByIdAsync(Guid userId);
}

public class AuthService : IAuthService
{
    private readonly ApplicationDbContext _context;
    private readonly IJwtService _jwtService;

    public AuthService(ApplicationDbContext context, IJwtService jwtService)
    {
        _context = context;
        _jwtService = jwtService;
    }

    public async Task<AuthResponse?> RegisterAsync(RegisterRequest request)
    {
        var email = NormalizeEmail(request.Email);

        // Check if user exists
        if (await _context.Users.AnyAsync(u => u.Email == email))
        {
            return null;
        }

        // Hash password
        var hashedPassword = BCrypt.Net.BCrypt.HashPassword(request.Password);

        // Create user
        var user = new User
        {
            Id = Guid.NewGuid(),
            Email = email,
            Password = hashedPassword,
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
            return null;
        }

        return BuildAuthResponse(user);
    }

    public async Task<AuthResponse?> LoginAsync(LoginRequest request)
    {
        var email = NormalizeEmail(request.Email);
        var user = await _context.Users.FirstOrDefaultAsync(u => u.Email == email);

        if (user == null || !BCrypt.Net.BCrypt.Verify(request.Password, user.Password))
        {
            return null;
        }

        if (!user.IsActive || user.IsLocked)
        {
            return null;
        }

        user.LastLoginAt = DateTime.UtcNow;
        await _context.SaveChangesAsync();

        return BuildAuthResponse(user);
    }

    /// <summary>Exchanges a valid refresh token for a fresh access/refresh token pair.</summary>
    public async Task<AuthResponse?> RefreshAsync(string refreshToken)
    {
        var userId = _jwtService.ValidateRefreshToken(refreshToken);
        if (userId == null) return null;

        var user = await _context.Users.FindAsync(userId.Value);
        if (user == null || !user.IsActive || user.IsLocked) return null;

        return BuildAuthResponse(user);
    }

    public async Task<UserDto?> GetUserByIdAsync(Guid userId)
    {
        var user = await _context.Users.FindAsync(userId);
        return user != null ? MapToUserDto(user) : null;
    }

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

    private static UserDto MapToUserDto(User user)
    {
        return new UserDto
        {
            Id = user.Id,
            Email = user.Email,
            FirstName = user.FirstName,
            LastName = user.LastName,
            Phone = user.Phone,
            Role = user.Role.ToString(),
            KycStatus = user.KycStatus.ToString(),
            IsActive = user.IsActive,
            CreatedAt = user.CreatedAt
        };
    }
}
