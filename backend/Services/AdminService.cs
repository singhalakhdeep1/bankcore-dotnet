using BankingAPI.Data;
using BankingAPI.DTOs;
using BankingAPI.Models;
using BankingAPI.Services.Security;
using Microsoft.EntityFrameworkCore;

namespace BankingAPI.Services;

public class AdminUserDto
{
    public Guid Id { get; set; }
    public string Email { get; set; } = string.Empty;
    public string FirstName { get; set; } = string.Empty;
    public string LastName { get; set; } = string.Empty;
    public string Role { get; set; } = string.Empty;
    public string KycStatus { get; set; } = string.Empty;
    public bool IsActive { get; set; }
    public bool IsLocked { get; set; }
    public DateTime? LockoutEnd { get; set; }
    public bool TwoFactorEnabled { get; set; }
    public int AccountCount { get; set; }
    public DateTime? LastLoginAt { get; set; }
    public DateTime CreatedAt { get; set; }
}

public class AdminAccountDto
{
    public Guid Id { get; set; }
    public Guid UserId { get; set; }
    public string OwnerEmail { get; set; } = string.Empty;
    public string AccountNumber { get; set; } = string.Empty;
    public string AccountType { get; set; } = string.Empty;
    public decimal Balance { get; set; }
    public string Currency { get; set; } = string.Empty;
    public string Status { get; set; } = string.Empty;
    public DateTime CreatedAt { get; set; }
}

public interface IAdminService
{
    Task<PagedResult<AdminUserDto>> ListUsersAsync(string? search, int page, int pageSize);
    Task<(bool Ok, string? Error)> SetRoleAsync(Guid targetUserId, UserRole role, Guid actorUserId);
    Task<(bool Ok, string? Error)> SetLockedAsync(Guid targetUserId, bool locked, Guid actorUserId);
    Task<PagedResult<AdminAccountDto>> ListAccountsAsync(string? search, AccountStatus? status, int page, int pageSize);
    Task<(bool Ok, string? Error)> SetAccountStatusAsync(Guid accountId, AccountStatus status, Guid actorUserId);
    Task<PagedResult<AuditLog>> ListAuditAsync(string? action, Guid? userId, int page, int pageSize);
}

/// <summary>Back-office operations for staff, managers and administrators. Every change is audited.</summary>
public class AdminService : IAdminService
{
    private readonly ApplicationDbContext _context;
    private readonly IAuditService _audit;

    public AdminService(ApplicationDbContext context, IAuditService audit)
    {
        _context = context;
        _audit = audit;
    }

    public async Task<PagedResult<AdminUserDto>> ListUsersAsync(string? search, int page, int pageSize)
    {
        page = Math.Max(1, page);
        pageSize = Math.Clamp(pageSize, 1, 100);

        var query = _context.Users.AsNoTracking().AsQueryable();
        if (!string.IsNullOrWhiteSpace(search))
        {
            var term = search.Trim().ToLowerInvariant();
            query = query.Where(u => u.Email.Contains(term)
                                     || u.FirstName.ToLower().Contains(term)
                                     || u.LastName.ToLower().Contains(term));
        }

        var total = await query.CountAsync();
        var items = await query
            .OrderByDescending(u => u.CreatedAt)
            .Skip((page - 1) * pageSize).Take(pageSize)
            .Select(u => new AdminUserDto
            {
                Id = u.Id,
                Email = u.Email,
                FirstName = u.FirstName,
                LastName = u.LastName,
                Role = u.Role.ToString(),
                KycStatus = u.KycStatus.ToString(),
                IsActive = u.IsActive,
                IsLocked = u.IsLocked,
                LockoutEnd = u.LockoutEnd,
                TwoFactorEnabled = u.TwoFactorEnabled,
                AccountCount = u.Accounts.Count,
                LastLoginAt = u.LastLoginAt,
                CreatedAt = u.CreatedAt
            })
            .ToListAsync();

        return new PagedResult<AdminUserDto> { Items = items, Page = page, PageSize = pageSize, TotalCount = total };
    }

    public async Task<(bool Ok, string? Error)> SetRoleAsync(Guid targetUserId, UserRole role, Guid actorUserId)
    {
        var user = await _context.Users.FirstOrDefaultAsync(u => u.Id == targetUserId);
        if (user == null) return (false, "User not found");
        if (targetUserId == actorUserId) return (false, "You cannot change your own role.");

        // Never leave the bank without an administrator.
        if (user.Role == UserRole.Admin && role != UserRole.Admin
            && await _context.Users.CountAsync(u => u.Role == UserRole.Admin && u.IsActive) <= 1)
        {
            return (false, "At least one active administrator is required.");
        }

        var previous = user.Role;
        user.Role = role;
        user.UpdatedAt = DateTime.UtcNow;
        await _context.SaveChangesAsync();
        await _audit.LogAsync("Admin.RoleChanged", actorUserId, "User", targetUserId.ToString(), $"{previous} -> {role}");
        return (true, null);
    }

    public async Task<(bool Ok, string? Error)> SetLockedAsync(Guid targetUserId, bool locked, Guid actorUserId)
    {
        var user = await _context.Users.FirstOrDefaultAsync(u => u.Id == targetUserId);
        if (user == null) return (false, "User not found");
        if (targetUserId == actorUserId) return (false, "You cannot lock your own account.");

        user.IsLocked = locked;
        if (!locked)
        {
            user.LockoutEnd = null;
            user.FailedLoginAttempts = 0;
        }

        user.UpdatedAt = DateTime.UtcNow;
        await _context.SaveChangesAsync();
        await _audit.LogAsync(locked ? "Admin.UserLocked" : "Admin.UserUnlocked", actorUserId, "User", targetUserId.ToString());
        return (true, null);
    }

    public async Task<PagedResult<AdminAccountDto>> ListAccountsAsync(string? search, AccountStatus? status, int page, int pageSize)
    {
        page = Math.Max(1, page);
        pageSize = Math.Clamp(pageSize, 1, 100);

        var query = _context.Accounts.AsNoTracking().AsQueryable();
        if (status.HasValue) query = query.Where(a => a.Status == status.Value);
        if (!string.IsNullOrWhiteSpace(search))
        {
            var term = search.Trim();
            query = query.Where(a => a.AccountNumber.Contains(term.ToUpper()) || a.User.Email.Contains(term.ToLower()));
        }

        var total = await query.CountAsync();
        var items = await query
            .OrderByDescending(a => a.CreatedAt)
            .Skip((page - 1) * pageSize).Take(pageSize)
            .Select(a => new AdminAccountDto
            {
                Id = a.Id,
                UserId = a.UserId,
                OwnerEmail = a.User.Email,
                AccountNumber = a.AccountNumber,
                AccountType = a.AccountType.ToString(),
                Balance = a.Balance,
                Currency = a.Currency,
                Status = a.Status.ToString(),
                CreatedAt = a.CreatedAt
            })
            .ToListAsync();

        return new PagedResult<AdminAccountDto> { Items = items, Page = page, PageSize = pageSize, TotalCount = total };
    }

    public async Task<(bool Ok, string? Error)> SetAccountStatusAsync(Guid accountId, AccountStatus status, Guid actorUserId)
    {
        var account = await _context.Accounts.FirstOrDefaultAsync(a => a.Id == accountId);
        if (account == null) return (false, "Account not found");
        if (status == AccountStatus.Closed) return (false, "Accounts are closed by their owner; use Frozen or Inactive here.");

        var previous = account.Status;
        account.Status = status;
        account.UpdatedAt = DateTime.UtcNow;
        await _context.SaveChangesAsync();
        await _audit.LogAsync("Admin.AccountStatusChanged", actorUserId, "Account", accountId.ToString(), $"{previous} -> {status}");
        return (true, null);
    }

    public async Task<PagedResult<AuditLog>> ListAuditAsync(string? action, Guid? userId, int page, int pageSize)
    {
        page = Math.Max(1, page);
        pageSize = Math.Clamp(pageSize, 1, 200);

        var query = _context.AuditLogs.AsNoTracking().AsQueryable();
        if (!string.IsNullOrWhiteSpace(action)) query = query.Where(a => a.Action.StartsWith(action));
        if (userId.HasValue) query = query.Where(a => a.UserId == userId.Value);

        return new PagedResult<AuditLog>
        {
            Page = page,
            PageSize = pageSize,
            TotalCount = await query.CountAsync(),
            Items = await query.OrderByDescending(a => a.Timestamp).Skip((page - 1) * pageSize).Take(pageSize).ToListAsync()
        };
    }
}
