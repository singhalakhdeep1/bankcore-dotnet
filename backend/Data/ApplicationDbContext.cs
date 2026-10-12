using Microsoft.EntityFrameworkCore;
using BankingAPI.Models;

namespace BankingAPI.Data;

public class ApplicationDbContext : DbContext
{
    public ApplicationDbContext(DbContextOptions<ApplicationDbContext> options)
        : base(options)
    {
    }

    public DbSet<User> Users { get; set; }
    public DbSet<Account> Accounts { get; set; }
    public DbSet<Transaction> Transactions { get; set; }
    public DbSet<LedgerEntry> LedgerEntries { get; set; }
    public DbSet<ComplianceAlert> ComplianceAlerts { get; set; }
    public DbSet<PaymentLog> PaymentLogs { get; set; }

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);

        // User configuration
        modelBuilder.Entity<User>(entity =>
        {
            entity.HasKey(e => e.Id);
            entity.HasIndex(e => e.Email).IsUnique();
            entity.Property(e => e.Email).IsRequired().HasMaxLength(255);
            entity.Property(e => e.FirstName).IsRequired().HasMaxLength(100);
            entity.Property(e => e.LastName).IsRequired().HasMaxLength(100);
            entity.Property(e => e.Role).HasConversion<string>().HasMaxLength(20);
            entity.Property(e => e.KycStatus).HasConversion<string>().HasMaxLength(20);
        });

        // Account configuration
        modelBuilder.Entity<Account>(entity =>
        {
            entity.HasKey(e => e.Id);
            entity.HasIndex(e => e.AccountNumber).IsUnique();
            entity.HasIndex(e => e.UserId);
            entity.Property(e => e.AccountNumber).IsRequired().HasMaxLength(50);
            entity.Property(e => e.Currency).IsRequired().HasMaxLength(3);
            entity.Property(e => e.Balance).HasPrecision(15, 2);
            entity.Property(e => e.OverdraftLimit).HasPrecision(15, 2);
            entity.Property(e => e.InterestRate).HasPrecision(5, 4);
            entity.Property(e => e.AccountType).HasConversion<string>().HasMaxLength(20);
            entity.Property(e => e.Status).HasConversion<string>().HasMaxLength(20);

            // Optimistic concurrency via PostgreSQL's xmin system column: two requests that
            // read the same balance cannot both write it back.
            entity.Property(e => e.Version)
                .HasColumnName("xmin")
                .HasColumnType("xid")
                .ValueGeneratedOnAddOrUpdate()
                .IsConcurrencyToken();

            entity.HasOne(e => e.User)
                .WithMany(u => u.Accounts)
                .HasForeignKey(e => e.UserId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        // Transaction configuration
        modelBuilder.Entity<Transaction>(entity =>
        {
            entity.HasKey(e => e.Id);
            // A transfer produces two legs (one per account) that share a reference number,
            // so uniqueness is per account, not global.
            entity.HasIndex(e => new { e.ReferenceNumber, e.AccountId }).IsUnique();
            entity.HasIndex(e => new { e.AccountId, e.CreatedAt });
            entity.Property(e => e.ReferenceNumber).IsRequired().HasMaxLength(100);
            entity.Property(e => e.Currency).IsRequired().HasMaxLength(3);
            entity.Property(e => e.Amount).HasPrecision(15, 2);
            entity.Property(e => e.BalanceBefore).HasPrecision(15, 2);
            entity.Property(e => e.BalanceAfter).HasPrecision(15, 2);
            entity.Property(e => e.Type).HasConversion<string>().HasMaxLength(20);
            entity.Property(e => e.Status).HasConversion<string>().HasMaxLength(20);

            entity.HasOne(e => e.Account)
                .WithMany(a => a.Transactions)
                .HasForeignKey(e => e.AccountId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        // Ledger configuration
        modelBuilder.Entity<LedgerEntry>(entity =>
        {
            entity.HasKey(e => e.Id);
            entity.HasIndex(e => new { e.AccountId, e.CreatedAt });
            entity.Property(e => e.Debit).HasPrecision(15, 2);
            entity.Property(e => e.Credit).HasPrecision(15, 2);
            entity.Property(e => e.BalanceAfter).HasPrecision(15, 2);
            entity.Property(e => e.Description).HasMaxLength(500);

            entity.HasOne(e => e.Account)
                .WithMany()
                .HasForeignKey(e => e.AccountId)
                .OnDelete(DeleteBehavior.Cascade);

            entity.HasOne(e => e.Transaction)
                .WithMany()
                .HasForeignKey(e => e.TransactionId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        // Compliance alert configuration
        modelBuilder.Entity<ComplianceAlert>(entity =>
        {
            entity.HasKey(e => e.Id);
            entity.HasIndex(e => new { e.AccountId, e.CreatedAt });
            entity.Property(e => e.AlertType).IsRequired().HasMaxLength(50);
            entity.Property(e => e.Severity).IsRequired().HasMaxLength(20);
            entity.Property(e => e.Description).HasMaxLength(500);

            entity.HasOne(e => e.Account)
                .WithMany()
                .HasForeignKey(e => e.AccountId)
                .OnDelete(DeleteBehavior.Cascade);

            entity.HasOne(e => e.Transaction)
                .WithMany()
                .HasForeignKey(e => e.TransactionId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        // Payment log configuration
        modelBuilder.Entity<PaymentLog>(entity =>
        {
            entity.HasKey(e => e.Id);
            entity.HasIndex(e => e.TransactionId).IsUnique();
            entity.HasIndex(e => new { e.AccountId, e.CreatedAt });
            entity.Property(e => e.TransactionId).IsRequired().HasMaxLength(64);
            entity.Property(e => e.GatewayTransactionId).HasMaxLength(64);
            entity.Property(e => e.Currency).IsRequired().HasMaxLength(3);
            entity.Property(e => e.MerchantName).HasMaxLength(200);
            entity.Property(e => e.Status).IsRequired().HasMaxLength(20);
            entity.Property(e => e.Amount).HasPrecision(15, 2);
            entity.Property(e => e.RefundAmount).HasPrecision(15, 2);

            entity.HasOne(e => e.Account)
                .WithMany()
                .HasForeignKey(e => e.AccountId)
                .OnDelete(DeleteBehavior.Cascade);
        });
    }
}
