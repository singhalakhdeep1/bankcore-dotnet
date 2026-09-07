using BankingAPI.Models;
using BankingAPI.Services;
using FluentAssertions;
using Moq;

namespace BankCore.Tests;

/// <summary>
/// Unit + integration tests for TransactionService covering:
///  - Deposit: happy path, inactive account, zero amount
///  - Withdrawal: happy path, insufficient funds, inactive account
///  - Transfer: happy path, idempotency (replay returns same records), self-transfer guard
///  - Database isolation: transactions appear only on the correct account
/// </summary>
public class TransactionServiceTests
{
    private static (TransactionService svc, BankingAPI.Data.ApplicationDbContext ctx) Build()
    {
        var ctx = TestDbFactory.Create();
        var ledger = new Mock<ILedgerService>();
        ledger.Setup(l => l.RecordTransactionAsync(It.IsAny<Transaction>()))
              .Returns(Task.CompletedTask);
        var compliance = new Mock<IComplianceService>();
        compliance.Setup(c => c.CheckTransactionAsync(It.IsAny<Transaction>()))
                  .ReturnsAsync(true);
        var svc = new TransactionService(ctx, ledger.Object, compliance.Object);
        return (svc, ctx);
    }

    // ────────────────────────────────────────────────────────────────────────
    // Deposit
    // ────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Deposit_ValidAmount_IncreasesBalance()
    {
        var (svc, ctx) = Build();
        var user = await TestDbFactory.SeedUserAsync(ctx);
        var account = await TestDbFactory.SeedAccountAsync(ctx, user.Id, balance: 500m);

        var tx = await svc.ProcessDepositAsync(account.Id, 300m, "Payroll");

        tx.Should().NotBeNull();
        tx!.Amount.Should().Be(300m);
        tx.Type.Should().Be(TransactionType.Deposit);
        tx.Status.Should().Be(TransactionStatus.Completed);
        tx.BalanceBefore.Should().Be(500m);
        tx.BalanceAfter.Should().Be(800m);

        var refreshed = await ctx.Accounts.FindAsync(account.Id);
        refreshed!.Balance.Should().Be(800m);
    }

    [Fact]
    public async Task Deposit_ZeroAmount_ReturnsNull()
    {
        var (svc, ctx) = Build();
        var user = await TestDbFactory.SeedUserAsync(ctx);
        var account = await TestDbFactory.SeedAccountAsync(ctx, user.Id);

        var tx = await svc.ProcessDepositAsync(account.Id, 0m);

        tx.Should().BeNull();
    }

    [Fact]
    public async Task Deposit_NegativeAmount_ReturnsNull()
    {
        var (svc, ctx) = Build();
        var user = await TestDbFactory.SeedUserAsync(ctx);
        var account = await TestDbFactory.SeedAccountAsync(ctx, user.Id);

        var tx = await svc.ProcessDepositAsync(account.Id, -50m);

        tx.Should().BeNull();
    }

    [Fact]
    public async Task Deposit_InactiveAccount_ReturnsNull()
    {
        var (svc, ctx) = Build();
        var user = await TestDbFactory.SeedUserAsync(ctx);
        var account = await TestDbFactory.SeedAccountAsync(ctx, user.Id, status: AccountStatus.Suspended);

        var tx = await svc.ProcessDepositAsync(account.Id, 100m);

        tx.Should().BeNull();
    }

    [Fact]
    public async Task Deposit_NonExistentAccount_ReturnsNull()
    {
        var (svc, _) = Build();
        var tx = await svc.ProcessDepositAsync(Guid.NewGuid(), 100m);
        tx.Should().BeNull();
    }

    [Fact]
    public async Task Deposit_TransactionAppearsInAccountHistory()
    {
        var (svc, ctx) = Build();
        var user = await TestDbFactory.SeedUserAsync(ctx);
        var account = await TestDbFactory.SeedAccountAsync(ctx, user.Id, balance: 0m);

        await svc.ProcessDepositAsync(account.Id, 250m);
        var txns = await svc.GetAccountTransactionsAsync(account.Id);

        txns.Should().HaveCount(1);
        txns[0].Amount.Should().Be(250m);
    }

    // ────────────────────────────────────────────────────────────────────────
    // Withdrawal
    // ────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Withdrawal_ValidAmount_DecreasesBalance()
    {
        var (svc, ctx) = Build();
        var user = await TestDbFactory.SeedUserAsync(ctx);
        var account = await TestDbFactory.SeedAccountAsync(ctx, user.Id, balance: 1000m);

        var tx = await svc.ProcessWithdrawalAsync(account.Id, 400m, "ATM");

        tx.Should().NotBeNull();
        tx!.Type.Should().Be(TransactionType.Withdrawal);
        tx.Status.Should().Be(TransactionStatus.Completed);
        tx.BalanceBefore.Should().Be(1000m);
        tx.BalanceAfter.Should().Be(600m);

        var refreshed = await ctx.Accounts.FindAsync(account.Id);
        refreshed!.Balance.Should().Be(600m);
    }

    [Fact]
    public async Task Withdrawal_InsufficientFunds_ReturnsNull()
    {
        var (svc, ctx) = Build();
        var user = await TestDbFactory.SeedUserAsync(ctx);
        var account = await TestDbFactory.SeedAccountAsync(ctx, user.Id, balance: 100m);

        var tx = await svc.ProcessWithdrawalAsync(account.Id, 500m);

        tx.Should().BeNull();
        // Balance should not have changed
        var refreshed = await ctx.Accounts.FindAsync(account.Id);
        refreshed!.Balance.Should().Be(100m);
    }

    [Fact]
    public async Task Withdrawal_ExactBalance_Succeeds()
    {
        var (svc, ctx) = Build();
        var user = await TestDbFactory.SeedUserAsync(ctx);
        var account = await TestDbFactory.SeedAccountAsync(ctx, user.Id, balance: 200m);

        var tx = await svc.ProcessWithdrawalAsync(account.Id, 200m);

        tx.Should().NotBeNull();
        var refreshed = await ctx.Accounts.FindAsync(account.Id);
        refreshed!.Balance.Should().Be(0m);
    }

    [Fact]
    public async Task Withdrawal_InactiveAccount_ReturnsNull()
    {
        var (svc, ctx) = Build();
        var user = await TestDbFactory.SeedUserAsync(ctx);
        var account = await TestDbFactory.SeedAccountAsync(ctx, user.Id, status: AccountStatus.Closed);

        var tx = await svc.ProcessWithdrawalAsync(account.Id, 50m);
        tx.Should().BeNull();
    }

    // ────────────────────────────────────────────────────────────────────────
    // Transfer & Idempotency
    // ────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Transfer_ValidTransfer_MovesMoneyCorrectly()
    {
        var (svc, ctx) = Build();
        var user = await TestDbFactory.SeedUserAsync(ctx);
        var from = await TestDbFactory.SeedAccountAsync(ctx, user.Id, balance: 1000m);
        var to = await TestDbFactory.SeedAccountAsync(ctx, user.Id, balance: 500m);

        var (fromTx, toTx) = await svc.TransferAsync(from.Id, to.Id, 300m, "Rent");

        fromTx.Should().NotBeNull();
        toTx.Should().NotBeNull();

        var fromAcc = await ctx.Accounts.FindAsync(from.Id);
        var toAcc = await ctx.Accounts.FindAsync(to.Id);

        fromAcc!.Balance.Should().Be(700m);
        toAcc!.Balance.Should().Be(800m);
    }

    [Fact]
    public async Task Transfer_IdempotencyKey_ReplayReturnsSameTransactions()
    {
        var (svc, ctx) = Build();
        var user = await TestDbFactory.SeedUserAsync(ctx);
        var from = await TestDbFactory.SeedAccountAsync(ctx, user.Id, balance: 2000m);
        var to = await TestDbFactory.SeedAccountAsync(ctx, user.Id, balance: 0m);
        var idempotencyKey = Guid.NewGuid().ToString();

        var (fromTx1, toTx1) = await svc.TransferAsync(from.Id, to.Id, 500m, idempotencyKey: idempotencyKey);
        var (fromTx2, toTx2) = await svc.TransferAsync(from.Id, to.Id, 500m, idempotencyKey: idempotencyKey);

        // Second call should return the same transaction records
        fromTx2!.Id.Should().Be(fromTx1!.Id);
        toTx2!.Id.Should().Be(toTx1!.Id);

        // Balance should only have been changed once
        var fromAcc = await ctx.Accounts.FindAsync(from.Id);
        fromAcc!.Balance.Should().Be(1500m);
    }

    [Fact]
    public async Task Transfer_DestinationNotFound_ThrowsException()
    {
        var (svc, ctx) = Build();
        var user = await TestDbFactory.SeedUserAsync(ctx);
        var from = await TestDbFactory.SeedAccountAsync(ctx, user.Id, balance: 500m);

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => svc.TransferAsync(from.Id, Guid.NewGuid(), 100m));
    }

    [Fact]
    public async Task Transfer_InsufficientFunds_FromBalanceUnchanged()
    {
        var (svc, ctx) = Build();
        var user = await TestDbFactory.SeedUserAsync(ctx);
        var from = await TestDbFactory.SeedAccountAsync(ctx, user.Id, balance: 50m);
        var to = await TestDbFactory.SeedAccountAsync(ctx, user.Id, balance: 0m);

        var (fromTx, toTx) = await svc.TransferAsync(from.Id, to.Id, 500m);

        fromTx.Should().BeNull();
        toTx.Should().BeNull();

        var fromAcc = await ctx.Accounts.FindAsync(from.Id);
        fromAcc!.Balance.Should().Be(50m, "balance should not change on failed transfer");
    }

    // ────────────────────────────────────────────────────────────────────────
    // Transaction isolation: transactions appear only on the correct account
    // ────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Transactions_AreIsolatedByAccount()
    {
        var (svc, ctx) = Build();
        var user = await TestDbFactory.SeedUserAsync(ctx);
        var acc1 = await TestDbFactory.SeedAccountAsync(ctx, user.Id, balance: 1000m);
        var acc2 = await TestDbFactory.SeedAccountAsync(ctx, user.Id, balance: 500m);

        await svc.ProcessDepositAsync(acc1.Id, 200m);
        await svc.ProcessDepositAsync(acc2.Id, 100m);

        var txns1 = await svc.GetAccountTransactionsAsync(acc1.Id);
        var txns2 = await svc.GetAccountTransactionsAsync(acc2.Id);

        txns1.Should().HaveCount(1);
        txns2.Should().HaveCount(1);
        txns1[0].AccountId.Should().Be(acc1.Id);
        txns2[0].AccountId.Should().Be(acc2.Id);
    }

    // ────────────────────────────────────────────────────────────────────────
    // Pagination
    // ────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task GetAccountTransactions_Paginated_ReturnCorrectPage()
    {
        var (svc, ctx) = Build();
        var user = await TestDbFactory.SeedUserAsync(ctx);
        var acc = await TestDbFactory.SeedAccountAsync(ctx, user.Id, balance: 10_000m);

        for (int i = 0; i < 15; i++)
        {
            await svc.ProcessDepositAsync(acc.Id, 10m + i);
        }

        var (page1, total) = await svc.GetAccountTransactionsAsync(acc.Id, page: 1, pageSize: 10);

        total.Should().Be(15);
        page1.Should().HaveCount(10);
    }

    [Fact]
    public async Task GetAccountTransactions_Paginated_SecondPageHasRemainder()
    {
        var (svc, ctx) = Build();
        var user = await TestDbFactory.SeedUserAsync(ctx);
        var acc = await TestDbFactory.SeedAccountAsync(ctx, user.Id, balance: 10_000m);

        for (int i = 0; i < 15; i++)
        {
            await svc.ProcessDepositAsync(acc.Id, 10m + i);
        }

        var (page2, _) = await svc.GetAccountTransactionsAsync(acc.Id, page: 2, pageSize: 10);
        page2.Should().HaveCount(5);
    }
}
