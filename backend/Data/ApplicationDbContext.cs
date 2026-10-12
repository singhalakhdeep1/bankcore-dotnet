using Microsoft.EntityFrameworkCore;
using BankingAPI.Models;

namespace BankingAPI.Data;

public class ApplicationDbContext : DbContext
{
    public ApplicationDbContext(DbContextOptions<ApplicationDbContext> options)
        : base(options)
    {
    }

    /// <summary>Lets derived contexts (the read-replica context) supply their own options type.</summary>
    protected ApplicationDbContext(DbContextOptions options)
        : base(options)
    {
    }

    public DbSet<User> Users { get; set; }
    public DbSet<Account> Accounts { get; set; }
    public DbSet<Transaction> Transactions { get; set; }
    public DbSet<LedgerEntry> LedgerEntries { get; set; }
    public DbSet<ComplianceAlert> ComplianceAlerts { get; set; }
    public DbSet<PaymentLog> PaymentLogs { get; set; }
    public DbSet<SanctionedEntity> SanctionedEntities { get; set; }
    public DbSet<RegulatoryReport> RegulatoryReports { get; set; }
    public DbSet<AuditLog> AuditLogs { get; set; }
    public DbSet<ApiKey> ApiKeys { get; set; }
    public DbSet<KycDocument> KycDocuments { get; set; }
    public DbSet<PaymentMethod> PaymentMethods { get; set; }
    public DbSet<RecurringPayment> RecurringPayments { get; set; }
    public DbSet<BankTransfer> BankTransfers { get; set; }
    public DbSet<Chargeback> Chargebacks { get; set; }
    public DbSet<LedgerPeriod> LedgerPeriods { get; set; }
    public DbSet<PeriodBalance> PeriodBalances { get; set; }
    public DbSet<GlEntry> GlEntries { get; set; }

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
            entity.Property(e => e.PasswordHash).IsRequired().HasMaxLength(100);
            entity.Property(e => e.TwoFactorSecret).HasMaxLength(64);
            entity.HasQueryFilter(e => !e.IsDeleted);
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

            entity.HasQueryFilter(e => !e.IsDeleted);

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
            entity.Property(e => e.GlCounter).HasConversion<string>().HasMaxLength(30);

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
            entity.HasIndex(e => e.Status);
            entity.Property(e => e.AlertType).IsRequired().HasMaxLength(50);
            entity.Property(e => e.Severity).IsRequired().HasMaxLength(20);
            entity.Property(e => e.Description).HasMaxLength(500);
            entity.Property(e => e.ReviewNotes).HasMaxLength(1000);
            entity.Property(e => e.Status).HasConversion<string>().HasMaxLength(20);

            entity.HasOne(e => e.Account)
                .WithMany()
                .HasForeignKey(e => e.AccountId)
                .OnDelete(DeleteBehavior.Cascade);

            entity.HasOne(e => e.Transaction)
                .WithMany()
                .HasForeignKey(e => e.TransactionId)
                .OnDelete(DeleteBehavior.SetNull);
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
            entity.Property(e => e.Fee).HasPrecision(15, 2);
            entity.Property(e => e.Kind).IsRequired().HasMaxLength(20);
            entity.Property(e => e.Provider).HasMaxLength(20);
            entity.Property(e => e.ProviderMode).HasMaxLength(20);

            entity.HasOne(e => e.Account)
                .WithMany()
                .HasForeignKey(e => e.AccountId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<SanctionedEntity>(entity =>
        {
            entity.HasKey(e => e.Id);
            entity.HasIndex(e => e.NormalizedName);
            entity.HasIndex(e => e.AccountNumber);
            entity.Property(e => e.Name).IsRequired().HasMaxLength(200);
            entity.Property(e => e.NormalizedName).IsRequired().HasMaxLength(200);
            entity.Property(e => e.ListName).HasMaxLength(50);
        });

        modelBuilder.Entity<RegulatoryReport>(entity =>
        {
            entity.HasKey(e => e.Id);
            entity.HasIndex(e => new { e.Type, e.GeneratedAt });
            entity.Property(e => e.Type).HasConversion<string>().HasMaxLength(30);
            entity.Property(e => e.PayloadJson).HasColumnType("jsonb");
        });

        modelBuilder.Entity<AuditLog>(entity =>
        {
            entity.HasKey(e => e.Id);
            entity.HasIndex(e => e.Timestamp);
            entity.HasIndex(e => new { e.UserId, e.Timestamp });
            entity.Property(e => e.Action).IsRequired().HasMaxLength(100);
            entity.Property(e => e.EntityType).HasMaxLength(50);
            entity.Property(e => e.EntityId).HasMaxLength(64);
            entity.Property(e => e.Details).HasMaxLength(2000);
            entity.Property(e => e.IpAddress).HasMaxLength(64);
            entity.Property(e => e.CorrelationId).HasMaxLength(64);
        });

        modelBuilder.Entity<ApiKey>(entity =>
        {
            entity.HasKey(e => e.Id);
            entity.HasIndex(e => e.Prefix).IsUnique();
            entity.Property(e => e.Name).IsRequired().HasMaxLength(100);
            entity.Property(e => e.Prefix).IsRequired().HasMaxLength(16);
            entity.Property(e => e.KeyHash).IsRequired().HasMaxLength(128);
            entity.Property(e => e.Scopes).HasMaxLength(200);
            entity.Ignore(e => e.IsActive);
        });

        modelBuilder.Entity<KycDocument>(entity =>
        {
            entity.HasKey(e => e.Id);
            entity.HasIndex(e => e.UserId);
            entity.Property(e => e.DocumentType).HasConversion<string>().HasMaxLength(30);
            entity.Property(e => e.Status).HasConversion<string>().HasMaxLength(20);
            entity.Property(e => e.OriginalFileName).HasMaxLength(260);
            entity.Property(e => e.StoredFileName).HasMaxLength(100);
            entity.Property(e => e.ContentType).HasMaxLength(100);
            entity.Property(e => e.Sha256).HasMaxLength(64);
            entity.Property(e => e.ReviewNotes).HasMaxLength(1000);

            entity.HasOne(e => e.User)
                .WithMany(u => u.KycDocuments)
                .HasForeignKey(e => e.UserId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<PaymentMethod>(entity =>
        {
            entity.HasKey(e => e.Id);
            entity.HasIndex(e => e.UserId);
            entity.Property(e => e.Provider).IsRequired().HasMaxLength(20);
            entity.Property(e => e.Token).IsRequired().HasMaxLength(200);
            entity.Property(e => e.Brand).HasMaxLength(30);
            entity.Property(e => e.Last4).HasMaxLength(4);

            entity.HasOne(e => e.User)
                .WithMany(u => u.PaymentMethods)
                .HasForeignKey(e => e.UserId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<RecurringPayment>(entity =>
        {
            entity.HasKey(e => e.Id);
            entity.HasIndex(e => new { e.Status, e.NextRunAt });
            entity.Property(e => e.Amount).HasPrecision(15, 2);
            entity.Property(e => e.Currency).IsRequired().HasMaxLength(3);
            entity.Property(e => e.MerchantName).IsRequired().HasMaxLength(200);
            entity.Property(e => e.LastResult).HasMaxLength(500);
            entity.Property(e => e.Frequency).HasConversion<string>().HasMaxLength(20);
            entity.Property(e => e.Status).HasConversion<string>().HasMaxLength(20);

            entity.HasOne(e => e.Account)
                .WithMany()
                .HasForeignKey(e => e.AccountId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<BankTransfer>(entity =>
        {
            entity.HasKey(e => e.Id);
            entity.HasIndex(e => new { e.Status, e.ExpectedSettlementAt });
            entity.HasIndex(e => e.Reference).IsUnique();
            entity.Property(e => e.Amount).HasPrecision(15, 2);
            entity.Property(e => e.Fee).HasPrecision(15, 2);
            entity.Property(e => e.Currency).IsRequired().HasMaxLength(3);
            entity.Property(e => e.BeneficiaryName).IsRequired().HasMaxLength(200);
            entity.Property(e => e.BeneficiaryAccountNumber).HasMaxLength(50);
            entity.Property(e => e.RoutingNumber).HasMaxLength(9);
            entity.Property(e => e.SwiftBic).HasMaxLength(11);
            entity.Property(e => e.Iban).HasMaxLength(34);
            entity.Property(e => e.DestinationCountry).HasMaxLength(2);
            entity.Property(e => e.Reference).IsRequired().HasMaxLength(64);
            entity.Property(e => e.FailureReason).HasMaxLength(500);
            entity.Property(e => e.Rail).HasConversion<string>().HasMaxLength(10);
            entity.Property(e => e.Status).HasConversion<string>().HasMaxLength(20);

            entity.HasOne(e => e.Account)
                .WithMany()
                .HasForeignKey(e => e.AccountId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<Chargeback>(entity =>
        {
            entity.HasKey(e => e.Id);
            entity.HasIndex(e => e.PaymentLogId);
            entity.HasIndex(e => e.Status);
            entity.Property(e => e.Amount).HasPrecision(15, 2);
            entity.Property(e => e.Reason).IsRequired().HasMaxLength(500);
            entity.Property(e => e.ResolutionNotes).HasMaxLength(1000);
            entity.Property(e => e.Status).HasConversion<string>().HasMaxLength(20);

            entity.HasOne(e => e.PaymentLog)
                .WithMany()
                .HasForeignKey(e => e.PaymentLogId)
                .OnDelete(DeleteBehavior.Cascade);

            entity.HasOne(e => e.Account)
                .WithMany()
                .HasForeignKey(e => e.AccountId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<LedgerPeriod>(entity =>
        {
            entity.HasKey(e => e.Id);
            entity.HasIndex(e => new { e.StartDate, e.EndDate });
            entity.Property(e => e.Name).IsRequired().HasMaxLength(100);
            entity.Property(e => e.TotalDebits).HasPrecision(18, 2);
            entity.Property(e => e.TotalCredits).HasPrecision(18, 2);
            entity.Property(e => e.Status).HasConversion<string>().HasMaxLength(10);
        });

        modelBuilder.Entity<PeriodBalance>(entity =>
        {
            entity.HasKey(e => e.Id);
            entity.HasIndex(e => new { e.PeriodId, e.AccountId }).IsUnique();
            entity.Property(e => e.Currency).IsRequired().HasMaxLength(3);
            entity.Property(e => e.Debits).HasPrecision(18, 2);
            entity.Property(e => e.Credits).HasPrecision(18, 2);
            entity.Property(e => e.ClosingBalance).HasPrecision(18, 2);

            entity.HasOne(e => e.Period)
                .WithMany(p => p.Balances)
                .HasForeignKey(e => e.PeriodId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<GlEntry>(entity =>
        {
            entity.HasKey(e => e.Id);
            entity.HasIndex(e => new { e.Account, e.CreatedAt });
            entity.HasIndex(e => e.TransactionId);
            entity.Property(e => e.Account).HasConversion<string>().HasMaxLength(30);
            entity.Property(e => e.Currency).IsRequired().HasMaxLength(3);
            entity.Property(e => e.Debit).HasPrecision(18, 2);
            entity.Property(e => e.Credit).HasPrecision(18, 2);
            entity.Property(e => e.Memo).HasMaxLength(200);
        });
    }
}
