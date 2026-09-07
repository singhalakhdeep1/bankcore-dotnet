using BankingAPI.Data;
using BankingAPI.Models;
using BankingAPI.Services;
using Microsoft.EntityFrameworkCore;
using Moq;

namespace BankCore.Tests;

/// <summary>
/// Shared test helper: creates an isolated in-memory EF context per test
/// so tests never interfere with each other.
/// </summary>
public static class TestDbFactory
{
    public static ApplicationDbContext Create(string? name = null)
    {
        var options = new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseInMemoryDatabase(name ?? Guid.NewGuid().ToString())
            .Options;
        return new ApplicationDbContext(options);
    }

    /// <summary>Creates a seeded user in the given context and returns the entity.</summary>
    public static async Task<User> SeedUserAsync(ApplicationDbContext ctx, string email = "test@bank.com")
    {
        var user = new User
        {
            Id = Guid.NewGuid(),
            Email = email,
            Password = BCrypt.Net.BCrypt.HashPassword("Password123!"),
            FirstName = "Test",
            LastName = "User",
            Role = UserRole.Customer,
            KycStatus = KycStatus.Pending,
            IsActive = true
        };
        ctx.Users.Add(user);
        await ctx.SaveChangesAsync();
        return user;
    }

    /// <summary>Creates an active checking account and returns the entity.</summary>
    public static async Task<Account> SeedAccountAsync(
        ApplicationDbContext ctx,
        Guid userId,
        decimal balance = 1000m,
        AccountStatus status = AccountStatus.Active)
    {
        var account = new Account
        {
            Id = Guid.NewGuid(),
            UserId = userId,
            AccountNumber = Guid.NewGuid().ToString("N")[..20].ToUpper(),
            AccountType = AccountType.Checking,
            Balance = balance,
            Currency = "USD",
            Status = status
        };
        ctx.Accounts.Add(account);
        await ctx.SaveChangesAsync();
        return account;
    }
}
