using BankingAPI.Models;
using BankingAPI.Services;
using BankingAPI.DTOs;
using FluentAssertions;

namespace BankCore.Tests;

/// <summary>
/// Tests for AccountService covering account creation, retrieval,
/// and user-scoped access control (users can only see their own accounts).
/// </summary>
public class AccountServiceTests
{
    private static (AccountService svc, BankingAPI.Data.ApplicationDbContext ctx) Build()
    {
        var ctx = TestDbFactory.Create();
        return (new AccountService(ctx), ctx);
    }

    // ────────────────────────────────────────────────────────────────────────
    // Legacy arithmetic tests (retained + expanded)
    // ────────────────────────────────────────────────────────────────────────

    [Fact]
    public void Account_Deposit_IncreasesBalance()
    {
        decimal initialBalance = 500.00m;
        decimal depositAmount = 250.00m;
        decimal newBalance = initialBalance + depositAmount;
        Assert.Equal(750.00m, newBalance);
    }

    [Fact]
    public void Account_Transfer_ValidatesFunds()
    {
        decimal balance = 1000.00m;
        decimal transferAmount = 1200.00m;
        bool canTransfer = balance >= transferAmount;
        Assert.False(canTransfer);
    }

    // ────────────────────────────────────────────────────────────────────────
    // Account creation
    // ────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task CreateAccount_ValidRequest_ReturnsAccountDto()
    {
        var (svc, ctx) = Build();
        var user = await TestDbFactory.SeedUserAsync(ctx);

        var dto = await svc.CreateAccountAsync(user.Id, new CreateAccountRequest
        {
            AccountType = "Checking",
            Currency = "USD"
        });

        dto.Should().NotBeNull();
        dto!.AccountType.Should().Be("Checking");
        dto.Currency.Should().Be("USD");
        dto.Balance.Should().Be(0m, "new accounts start at zero");
        dto.Status.Should().Be("Active");
    }

    [Fact]
    public async Task CreateAccount_DefaultsCurrencyToUsd()
    {
        var (svc, ctx) = Build();
        var user = await TestDbFactory.SeedUserAsync(ctx);

        var dto = await svc.CreateAccountAsync(user.Id, new CreateAccountRequest
        {
            AccountType = "Savings"
        });

        dto!.Currency.Should().Be("USD");
    }

    [Fact]
    public async Task CreateAccount_InvalidAccountType_ReturnsNull()
    {
        var (svc, ctx) = Build();
        var user = await TestDbFactory.SeedUserAsync(ctx);

        var dto = await svc.CreateAccountAsync(user.Id, new CreateAccountRequest
        {
            AccountType = "InvalidType"
        });

        dto.Should().BeNull();
    }

    [Fact]
    public async Task CreateAccount_AccountNumberIsUnique()
    {
        var (svc, ctx) = Build();
        var user = await TestDbFactory.SeedUserAsync(ctx);

        var dto1 = await svc.CreateAccountAsync(user.Id, new CreateAccountRequest { AccountType = "Checking" });
        var dto2 = await svc.CreateAccountAsync(user.Id, new CreateAccountRequest { AccountType = "Savings" });

        dto1!.AccountNumber.Should().NotBe(dto2!.AccountNumber);
    }

    // ────────────────────────────────────────────────────────────────────────
    // Retrieval
    // ────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task GetUserAccounts_ReturnsOnlyOwnAccounts()
    {
        var (svc, ctx) = Build();
        var user1 = await TestDbFactory.SeedUserAsync(ctx, "user1@bank.com");
        var user2 = await TestDbFactory.SeedUserAsync(ctx, "user2@bank.com");

        await TestDbFactory.SeedAccountAsync(ctx, user1.Id);
        await TestDbFactory.SeedAccountAsync(ctx, user1.Id);
        await TestDbFactory.SeedAccountAsync(ctx, user2.Id);

        var user1Accounts = await svc.GetUserAccountsAsync(user1.Id);
        var user2Accounts = await svc.GetUserAccountsAsync(user2.Id);

        user1Accounts.Should().HaveCount(2, "user1 has 2 accounts");
        user2Accounts.Should().HaveCount(1, "user2 has 1 account");
        user1Accounts.All(a => a.Id != user2Accounts[0].Id).Should().BeTrue();
    }

    [Fact]
    public async Task GetUserAccounts_NoAccounts_ReturnsEmptyList()
    {
        var (svc, ctx) = Build();
        var user = await TestDbFactory.SeedUserAsync(ctx);

        var accounts = await svc.GetUserAccountsAsync(user.Id);
        accounts.Should().BeEmpty();
    }

    [Fact]
    public async Task GetAccountById_OwnerCanAccess()
    {
        var (svc, ctx) = Build();
        var user = await TestDbFactory.SeedUserAsync(ctx);
        var account = await TestDbFactory.SeedAccountAsync(ctx, user.Id, balance: 750m);

        var dto = await svc.GetAccountByIdAsync(account.Id, user.Id);

        dto.Should().NotBeNull();
        dto!.Balance.Should().Be(750m);
    }

    [Fact]
    public async Task GetAccountById_WrongUser_ReturnsNull()
    {
        var (svc, ctx) = Build();
        var owner = await TestDbFactory.SeedUserAsync(ctx, "owner@bank.com");
        var other = await TestDbFactory.SeedUserAsync(ctx, "other@bank.com");
        var account = await TestDbFactory.SeedAccountAsync(ctx, owner.Id);

        var dto = await svc.GetAccountByIdAsync(account.Id, other.Id);

        dto.Should().BeNull("cross-user access must be denied");
    }

    [Fact]
    public async Task GetAccountById_NonExistentAccount_ReturnsNull()
    {
        var (svc, ctx) = Build();
        var user = await TestDbFactory.SeedUserAsync(ctx);

        var dto = await svc.GetAccountByIdAsync(Guid.NewGuid(), user.Id);
        dto.Should().BeNull();
    }
}
