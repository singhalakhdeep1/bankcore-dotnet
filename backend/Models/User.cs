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
    public string Password { get; set; } = string.Empty;
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
    public bool IsLocked { get; set; } = false;
    public bool TwoFactorEnabled { get; set; } = false;
    public DateTime? LastLoginAt { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;

    // Navigation properties
    public ICollection<Account> Accounts { get; set; } = new List<Account>();
}
