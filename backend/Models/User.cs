namespace BankingAPI.Models;

public enum UserRole
{
    Customer,
    Staff,
    Manager,
    Admin
}

public enum KycStatus
{
    Pending,
    Verified,
    Rejected
}

public class User
{
    public Guid Id { get; set; }
    public string Email { get; set; } = string.Empty;
    public string PasswordHash { get; set; } = string.Empty;
    public string FirstName { get; set; } = string.Empty;
    public string LastName { get; set; } = string.Empty;
    public string? Phone { get; set; }
    public DateTime? DateOfBirth { get; set; }
    public string? Address { get; set; }
    public string? City { get; set; }
    public string? State { get; set; }
    public string? Country { get; set; }
    public string? PostalCode { get; set; }
    public UserRole Role { get; set; } = UserRole.Customer;
    public KycStatus KycStatus { get; set; } = KycStatus.Pending;
    public bool IsActive { get; set; } = true;

    /// <summary>Manually locked by staff. Automatic lockouts use <see cref="LockoutEnd"/> instead.</summary>
    public bool IsLocked { get; set; } = false;

    /// <summary>Consecutive failed logins since the last success.</summary>
    public int FailedLoginAttempts { get; set; }

    /// <summary>While in the future, the account is temporarily locked after too many failed logins.</summary>
    public DateTime? LockoutEnd { get; set; }

    public bool TwoFactorEnabled { get; set; } = false;

    /// <summary>Base32 TOTP secret. Set during enrolment; <see cref="TwoFactorEnabled"/> turns on after the first valid code.</summary>
    public string? TwoFactorSecret { get; set; }

    public DateTime? LastLoginAt { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;

    /// <summary>Soft delete: rows are hidden by a global query filter but kept for audit and ledger integrity.</summary>
    public bool IsDeleted { get; set; }
    public DateTime? DeletedAt { get; set; }

    // Navigation properties
    public ICollection<Account> Accounts { get; set; } = new List<Account>();
    public ICollection<KycDocument> KycDocuments { get; set; } = new List<KycDocument>();
    public ICollection<PaymentMethod> PaymentMethods { get; set; } = new List<PaymentMethod>();
}
