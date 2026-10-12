using BankingAPI.Data;
using BankingAPI.Models;
using Microsoft.EntityFrameworkCore;

namespace BankingAPI.Services;

public interface IComplianceService
{
    Task CheckTransactionAsync(Transaction transaction);
    Task<List<ComplianceAlert>> GetComplianceAlertsAsync(Guid? accountId = null);
    Task<bool> IsAccountCompliantAsync(Guid accountId);
    Task<ComplianceReport> GenerateComplianceReportAsync(Guid accountId, DateTime startDate, DateTime endDate);
}

public class ComplianceService : IComplianceService
{
    private readonly ApplicationDbContext _context;

    public ComplianceService(ApplicationDbContext context)
    {
        _context = context;
    }

    public async Task CheckTransactionAsync(Transaction transaction)
    {
        var alerts = new List<ComplianceAlert>();

        // Check for large transactions
        if (Math.Abs(transaction.Amount) > 10000)
        {
            alerts.Add(new ComplianceAlert
            {
                Id = Guid.NewGuid(),
                AccountId = transaction.AccountId,
                TransactionId = transaction.Id,
                AlertType = "LargeTransaction",
                Severity = "Medium",
                Description = $"Transaction amount ${transaction.Amount:N2} exceeds $10,000 threshold",
                CreatedAt = DateTime.UtcNow
            });
        }

        // Check for high-frequency transactions
        var recentTransactions = await _context.Transactions
            .Where(t => t.AccountId == transaction.AccountId &&
                       t.CreatedAt >= DateTime.UtcNow.AddHours(-1) &&
                       t.Status == TransactionStatus.Completed)
            .CountAsync();

        if (recentTransactions > 10)
        {
            alerts.Add(new ComplianceAlert
            {
                Id = Guid.NewGuid(),
                AccountId = transaction.AccountId,
                TransactionId = transaction.Id,
                AlertType = "HighFrequency",
                Severity = "High",
                Description = $"More than 10 transactions in the last hour",
                CreatedAt = DateTime.UtcNow
            });
        }

        // Check for unusual patterns
        var account = await _context.Accounts.FindAsync(transaction.AccountId);
        if (account != null)
        {
            // Average of the account's *previous* completed transactions (nullable so an empty history is fine).
            var avgTransaction = await _context.Transactions
                .Where(t => t.AccountId == transaction.AccountId &&
                           t.Id != transaction.Id &&
                           t.Status == TransactionStatus.Completed)
                .AverageAsync(t => (decimal?)Math.Abs(t.Amount)) ?? 0m;

            if (avgTransaction > 0 && Math.Abs(transaction.Amount) > avgTransaction * 5)
            {
                alerts.Add(new ComplianceAlert
                {
                    Id = Guid.NewGuid(),
                    AccountId = transaction.AccountId,
                    TransactionId = transaction.Id,
                    AlertType = "UnusualAmount",
                    Severity = "Medium",
                    Description = $"Transaction amount is 5x higher than average",
                    CreatedAt = DateTime.UtcNow
                });
            }
        }

        // Mark transaction as potentially fraudulent if high severity alerts
        if (alerts.Any(a => a.Severity == "High"))
        {
            transaction.IsFraudulent = true;
            await _context.SaveChangesAsync();
        }

        // Save alerts
        if (alerts.Any())
        {
            _context.ComplianceAlerts.AddRange(alerts);
            await _context.SaveChangesAsync();
        }
    }

    public async Task<List<ComplianceAlert>> GetComplianceAlertsAsync(Guid? accountId = null)
    {
        var query = _context.ComplianceAlerts.AsQueryable();

        if (accountId.HasValue)
        {
            query = query.Where(a => a.AccountId == accountId.Value);
        }

        return await query
            .OrderByDescending(a => a.CreatedAt)
            .Take(100)
            .ToListAsync();
    }

    public async Task<bool> IsAccountCompliantAsync(Guid accountId)
    {
        var recentAlerts = await _context.ComplianceAlerts
            .Where(a => a.AccountId == accountId &&
                       a.CreatedAt >= DateTime.UtcNow.AddDays(-30) &&
                       a.Severity == "High")
            .CountAsync();

        return recentAlerts == 0;
    }

    public async Task<ComplianceReport> GenerateComplianceReportAsync(Guid accountId, DateTime startDate, DateTime endDate)
    {
        var transactions = await _context.Transactions
            .Where(t => t.AccountId == accountId &&
                       t.CreatedAt >= startDate &&
                       t.CreatedAt <= endDate)
            .ToListAsync();

        var alerts = await _context.ComplianceAlerts
            .Where(a => a.AccountId == accountId &&
                       a.CreatedAt >= startDate &&
                       a.CreatedAt <= endDate)
            .ToListAsync();

        var totalVolume = transactions.Where(t => t.Status == TransactionStatus.Completed)
            .Sum(t => Math.Abs(t.Amount));

        var highRiskTransactions = transactions.Count(t => t.IsFraudulent);

        return new ComplianceReport
        {
            AccountId = accountId,
            StartDate = startDate,
            EndDate = endDate,
            TotalTransactions = transactions.Count,
            TotalVolume = totalVolume,
            HighRiskTransactions = highRiskTransactions,
            TotalAlerts = alerts.Count,
            HighSeverityAlerts = alerts.Count(a => a.Severity == "High"),
            MediumSeverityAlerts = alerts.Count(a => a.Severity == "Medium"),
            LowSeverityAlerts = alerts.Count(a => a.Severity == "Low"),
            ComplianceScore = CalculateComplianceScore(transactions, alerts)
        };
    }

    private static int CalculateComplianceScore(List<Transaction> transactions, List<ComplianceAlert> alerts)
    {
        var score = 100;

        // Deduct for high-risk transactions
        score -= transactions.Count(t => t.IsFraudulent) * 10;

        // Deduct for alerts
        score -= alerts.Count(a => a.Severity == "High") * 15;
        score -= alerts.Count(a => a.Severity == "Medium") * 5;
        score -= alerts.Count(a => a.Severity == "Low") * 2;

        return Math.Max(0, score);
    }
}
public class ComplianceReport
{
    public Guid AccountId { get; set; }
    public DateTime StartDate { get; set; }
    public DateTime EndDate { get; set; }
    public int TotalTransactions { get; set; }
    public decimal TotalVolume { get; set; }
    public int HighRiskTransactions { get; set; }
    public int TotalAlerts { get; set; }
    public int HighSeverityAlerts { get; set; }
    public int MediumSeverityAlerts { get; set; }
    public int LowSeverityAlerts { get; set; }
    public int ComplianceScore { get; set; }
}
