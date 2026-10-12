using System.Text.Json;
using BankingAPI.Data;
using BankingAPI.Models;
using BankingAPI.Services.Security;
using Microsoft.EntityFrameworkCore;

namespace BankingAPI.Services;

/// <summary>The outcome of the pre-transaction compliance check.</summary>
public class ComplianceDecision
{
    public bool Allowed { get; set; } = true;
    public string? Code { get; set; }
    public string? Reason { get; set; }
    public int RiskScore { get; set; }

    public static ComplianceDecision Allow(int risk = 0) => new() { Allowed = true, RiskScore = risk };
    public static ComplianceDecision Deny(string code, string reason, int risk = 100) =>
        new() { Allowed = false, Code = code, Reason = reason, RiskScore = risk };
}

public class AlertPage
{
    public List<ComplianceAlert> Items { get; set; } = new();
    public int Page { get; set; }
    public int PageSize { get; set; }
    public int TotalCount { get; set; }
}

public interface IComplianceService
{
    /// <summary>Post-transaction monitoring: raises alerts (large amount, CTR, structuring, frequency, IP spread).</summary>
    Task CheckTransactionAsync(Transaction transaction);

    /// <summary>Pre-transaction gate: KYC status and limits, sanctions screening of the counterparty, risk hold.</summary>
    Task<ComplianceDecision> EvaluateAsync(
        Guid accountId, decimal amount, TransactionType type,
        string? counterpartyName = null, string? counterpartyAccountNumber = null);

    /// <summary>Daily debit limit for the account, depending on the owner's KYC status.</summary>
    Task<decimal> GetDailyDebitLimitAsync(Guid accountId);

    /// <summary>0-100 score from the alerts raised in the last 30 days.</summary>
    Task<int> GetRiskScoreAsync(Guid accountId);

    Task<List<ComplianceAlert>> GetComplianceAlertsAsync(Guid? accountId = null);
    Task<AlertPage> ListAlertsAsync(AlertStatus? status, string? severity, int page, int pageSize);
    Task<ComplianceAlert?> ReviewAlertAsync(Guid alertId, AlertStatus status, string? notes, Guid reviewerUserId);
    Task<bool> IsAccountCompliantAsync(Guid accountId);
    Task<ComplianceReport> GenerateComplianceReportAsync(Guid accountId, DateTime startDate, DateTime endDate);
    Task<RegulatoryReport> GenerateRegulatoryReportAsync(ReportType type, DateTime start, DateTime end, Guid generatedByUserId);
}

public class ComplianceService : IComplianceService
{
    public const decimal ReportingThreshold = 10_000m;
    public const decimal KycPendingDailyLimit = 2_500m;
    public const decimal KycVerifiedDailyLimit = 10_000m;

    private readonly ApplicationDbContext _context;
    private readonly ISanctionsService _sanctions;
    private readonly IAuditService _audit;

    public ComplianceService(ApplicationDbContext context, ISanctionsService sanctions, IAuditService audit)
    {
        _context = context;
        _sanctions = sanctions;
        _audit = audit;
    }

    // ------------------------------------------------------------------ pre-transaction gate

    public async Task<ComplianceDecision> EvaluateAsync(
        Guid accountId, decimal amount, TransactionType type,
        string? counterpartyName = null, string? counterpartyAccountNumber = null)
    {
        var account = await _context.Accounts.AsNoTracking().Include(a => a.User).FirstOrDefaultAsync(a => a.Id == accountId);
        if (account == null) return ComplianceDecision.Deny("ACCOUNT", "Account not found.");

        var kyc = account.User.KycStatus;
        if (kyc == KycStatus.Rejected)
        {
            return ComplianceDecision.Deny("KYC_REJECTED", "Identity verification was rejected. Contact support.");
        }

        var isOutgoing = type is TransactionType.Withdrawal or TransactionType.Payment or TransactionType.Transfer
                         or TransactionType.ExternalTransfer;
        if (!isOutgoing) return ComplianceDecision.Allow();

        if (kyc == KycStatus.Pending && amount > KycPendingDailyLimit)
        {
            return ComplianceDecision.Deny("KYC_LIMIT",
                $"Complete identity verification (KYC) to send more than {KycPendingDailyLimit:N0} in one transaction.", 40);
        }

        if (!string.IsNullOrWhiteSpace(counterpartyName) || !string.IsNullOrWhiteSpace(counterpartyAccountNumber))
        {
            var hit = await _sanctions.ScreenAsync(counterpartyName, counterpartyAccountNumber);
            if (hit != null)
            {
                _context.ComplianceAlerts.Add(NewAlert(accountId, null, "SanctionsMatch", "High", 100,
                    $"Blocked {type} of {amount:N2}: counterparty matches {hit.ListName} entry '{hit.Name}'."));
                await _context.SaveChangesAsync();
                await _audit.LogAsync("Compliance.SanctionsBlock", account.UserId, "Account", accountId.ToString(),
                    $"Counterparty '{counterpartyName ?? counterpartyAccountNumber}' matched {hit.ListName}");

                return ComplianceDecision.Deny("SANCTIONS", "This payment cannot be processed because the recipient is on a sanctions list.");
            }
        }

        var risk = await GetRiskScoreAsync(accountId);
        if (risk >= 80 && amount > 1_000m)
        {
            return ComplianceDecision.Deny("RISK_HOLD", "This account is under compliance review. Large payments are on hold.", risk);
        }

        return ComplianceDecision.Allow(risk);
    }

    public async Task<decimal> GetDailyDebitLimitAsync(Guid accountId)
    {
        var kyc = await _context.Accounts.AsNoTracking()
            .Where(a => a.Id == accountId)
            .Select(a => a.User.KycStatus)
            .FirstOrDefaultAsync();

        return kyc switch
        {
            KycStatus.Verified => KycVerifiedDailyLimit,
            KycStatus.Pending => KycPendingDailyLimit,
            _ => 0m
        };
    }

    // ------------------------------------------------------------------ post-transaction monitoring

    public async Task CheckTransactionAsync(Transaction transaction)
    {
        var now = DateTime.UtcNow;
        var alerts = new List<ComplianceAlert>();
        var absolute = Math.Abs(transaction.Amount);
        var isCash = transaction.Type is TransactionType.Deposit or TransactionType.Withdrawal;

        // Large transaction
        if (absolute > ReportingThreshold)
        {
            alerts.Add(NewAlert(transaction.AccountId, transaction.Id, "LargeTransaction", "Medium", 30,
                $"Transaction amount {absolute:N2} exceeds the {ReportingThreshold:N0} threshold."));
        }

        // Currency transaction report: cash movements above the threshold in one UTC day (once per account per day)
        if (isCash)
        {
            var dayStart = now.Date;
            var cashToday = await _context.Transactions.AsNoTracking()
                .Where(t => t.AccountId == transaction.AccountId && t.CreatedAt >= dayStart
                            && t.Status == TransactionStatus.Completed
                            && (t.Type == TransactionType.Deposit || t.Type == TransactionType.Withdrawal))
                .SumAsync(t => t.Amount < 0 ? -t.Amount : t.Amount);

            if (cashToday > ReportingThreshold && !await AlertExistsTodayAsync(transaction.AccountId, "CurrencyReport"))
            {
                alerts.Add(NewAlert(transaction.AccountId, transaction.Id, "CurrencyReport", "Medium", 25,
                    $"Cash activity of {cashToday:N2} today exceeds the {ReportingThreshold:N0} reporting threshold (CTR)."));
            }

            // Structuring: several cash movements kept just under the threshold that add up to more than it
            var recentCash = await _context.Transactions.AsNoTracking()
                .Where(t => t.AccountId == transaction.AccountId && t.CreatedAt >= now.AddHours(-24)
                            && t.Status == TransactionStatus.Completed
                            && (t.Type == TransactionType.Deposit || t.Type == TransactionType.Withdrawal))
                .Select(t => t.Amount < 0 ? -t.Amount : t.Amount)
                .ToListAsync();

            var nearThreshold = recentCash.Where(a => a >= 3_000m && a < ReportingThreshold).ToList();
            if (nearThreshold.Count >= 3 && nearThreshold.Sum() > ReportingThreshold
                && !await AlertExistsTodayAsync(transaction.AccountId, "Structuring"))
            {
                alerts.Add(NewAlert(transaction.AccountId, transaction.Id, "Structuring", "High", 60,
                    $"{nearThreshold.Count} cash movements of 3,000-10,000 in 24 hours totalling {nearThreshold.Sum():N2} (possible structuring)."));
            }
        }

        // High frequency
        var lastHour = await _context.Transactions.AsNoTracking()
            .CountAsync(t => t.AccountId == transaction.AccountId && t.CreatedAt >= now.AddHours(-1)
                             && t.Status == TransactionStatus.Completed);
        if (lastHour > 10)
        {
            alerts.Add(NewAlert(transaction.AccountId, transaction.Id, "HighFrequency", "High", 50,
                "More than 10 transactions in the last hour."));
        }

        // Unusual amount compared with this account's history
        var average = await _context.Transactions.AsNoTracking()
            .Where(t => t.AccountId == transaction.AccountId && t.Id != transaction.Id && t.Status == TransactionStatus.Completed)
            .AverageAsync(t => (decimal?)(t.Amount < 0 ? -t.Amount : t.Amount)) ?? 0m;
        if (average > 0 && absolute > average * 5)
        {
            alerts.Add(NewAlert(transaction.AccountId, transaction.Id, "UnusualAmount", "Medium", 25,
                $"Transaction amount is more than 5x this account's average of {average:N2}."));
        }

        // Many different IP addresses in a week
        if (!string.IsNullOrEmpty(transaction.IpAddress))
        {
            var distinctIps = await _context.Transactions.AsNoTracking()
                .Where(t => t.AccountId == transaction.AccountId && t.CreatedAt >= now.AddDays(-7) && t.IpAddress != null)
                .Select(t => t.IpAddress)
                .Distinct()
                .CountAsync();

            if (distinctIps > 5 && !await AlertExistsTodayAsync(transaction.AccountId, "MultipleIps"))
            {
                alerts.Add(NewAlert(transaction.AccountId, transaction.Id, "MultipleIps", "High", 50,
                    $"Activity from {distinctIps} different IP addresses in 7 days."));
            }
        }

        if (alerts.Count == 0) return;

        // Flag the transaction as suspicious when any rule rated it High
        if (alerts.Any(a => a.Severity == "High"))
        {
            var tracked = await _context.Transactions.FindAsync(transaction.Id);
            if (tracked != null) tracked.IsFraudulent = true;
        }

        _context.ComplianceAlerts.AddRange(alerts);
        await _context.SaveChangesAsync();
    }

    public async Task<int> GetRiskScoreAsync(Guid accountId)
    {
        var since = DateTime.UtcNow.AddDays(-30);
        var score = await _context.ComplianceAlerts.AsNoTracking()
            .Where(a => a.AccountId == accountId && a.CreatedAt >= since && a.Status != AlertStatus.Dismissed)
            .SumAsync(a => a.RiskScore);
        return Math.Min(100, score);
    }

    // ------------------------------------------------------------------ alerts

    public async Task<List<ComplianceAlert>> GetComplianceAlertsAsync(Guid? accountId = null)
    {
        var query = _context.ComplianceAlerts.AsNoTracking().AsQueryable();
        if (accountId.HasValue) query = query.Where(a => a.AccountId == accountId.Value);

        return await query.OrderByDescending(a => a.CreatedAt).Take(100).ToListAsync();
    }

    public async Task<AlertPage> ListAlertsAsync(AlertStatus? status, string? severity, int page, int pageSize)
    {
        page = Math.Max(1, page);
        pageSize = Math.Clamp(pageSize, 1, 100);

        var query = _context.ComplianceAlerts.AsNoTracking().AsQueryable();
        if (status.HasValue) query = query.Where(a => a.Status == status.Value);
        if (!string.IsNullOrWhiteSpace(severity)) query = query.Where(a => a.Severity == severity);

        return new AlertPage
        {
            Page = page,
            PageSize = pageSize,
            TotalCount = await query.CountAsync(),
            Items = await query.OrderByDescending(a => a.CreatedAt).Skip((page - 1) * pageSize).Take(pageSize).ToListAsync()
        };
    }

    public async Task<ComplianceAlert?> ReviewAlertAsync(Guid alertId, AlertStatus status, string? notes, Guid reviewerUserId)
    {
        var alert = await _context.ComplianceAlerts.FindAsync(alertId);
        if (alert == null) return null;

        alert.Status = status;
        alert.ReviewNotes = notes;
        alert.ReviewedByUserId = reviewerUserId;
        alert.ReviewedAt = DateTime.UtcNow;
        await _context.SaveChangesAsync();

        await _audit.LogAsync("Compliance.AlertReviewed", reviewerUserId, "ComplianceAlert", alertId.ToString(), $"{status}: {notes}");
        return alert;
    }

    public async Task<bool> IsAccountCompliantAsync(Guid accountId)
    {
        var since = DateTime.UtcNow.AddDays(-30);
        var highAlerts = await _context.ComplianceAlerts.AsNoTracking()
            .CountAsync(a => a.AccountId == accountId && a.CreatedAt >= since
                             && a.Severity == "High" && a.Status != AlertStatus.Dismissed);
        return highAlerts == 0;
    }

    public async Task<ComplianceReport> GenerateComplianceReportAsync(Guid accountId, DateTime startDate, DateTime endDate)
    {
        var transactions = await _context.Transactions.AsNoTracking()
            .Where(t => t.AccountId == accountId && t.CreatedAt >= startDate && t.CreatedAt <= endDate)
            .ToListAsync();

        var alerts = await _context.ComplianceAlerts.AsNoTracking()
            .Where(a => a.AccountId == accountId && a.CreatedAt >= startDate && a.CreatedAt <= endDate)
            .ToListAsync();

        var totalVolume = transactions.Where(t => t.Status == TransactionStatus.Completed).Sum(t => Math.Abs(t.Amount));

        return new ComplianceReport
        {
            AccountId = accountId,
            StartDate = startDate,
            EndDate = endDate,
            TotalTransactions = transactions.Count,
            TotalVolume = totalVolume,
            HighRiskTransactions = transactions.Count(t => t.IsFraudulent),
            TotalAlerts = alerts.Count,
            HighSeverityAlerts = alerts.Count(a => a.Severity == "High"),
            MediumSeverityAlerts = alerts.Count(a => a.Severity == "Medium"),
            LowSeverityAlerts = alerts.Count(a => a.Severity == "Low"),
            ComplianceScore = CalculateComplianceScore(transactions, alerts),
            RiskScore = await GetRiskScoreAsync(accountId)
        };
    }

    // ------------------------------------------------------------------ regulatory reports

    public async Task<RegulatoryReport> GenerateRegulatoryReportAsync(ReportType type, DateTime start, DateTime end, Guid generatedByUserId)
    {
        var from = DateTime.SpecifyKind(start.Date, DateTimeKind.Utc);
        var to = DateTime.SpecifyKind(end.Date, DateTimeKind.Utc).AddDays(1);

        object payload;
        int count;

        switch (type)
        {
            case ReportType.SuspiciousActivity:
            {
                var alerts = await _context.ComplianceAlerts.AsNoTracking()
                    .Where(a => a.CreatedAt >= from && a.CreatedAt < to && a.Status != AlertStatus.Dismissed
                                && (a.Severity == "High" || a.Status == AlertStatus.Escalated))
                    .Include(a => a.Account).ThenInclude(acc => acc.User)
                    .Include(a => a.Transaction)
                    .OrderBy(a => a.CreatedAt)
                    .ToListAsync();

                var items = alerts.Select(a => new
                {
                    alertId = a.Id,
                    a.AlertType,
                    a.Severity,
                    status = a.Status.ToString(),
                    a.RiskScore,
                    a.Description,
                    a.CreatedAt,
                    accountNumber = a.Account.AccountNumber,
                    customer = $"{a.Account.User.FirstName} {a.Account.User.LastName}",
                    customerId = a.Account.UserId,
                    transactionId = a.TransactionId,
                    amount = a.Transaction?.Amount
                }).ToList();

                count = items.Count;
                payload = new { type = "SAR", from, to = to.AddDays(-1), items };
                break;
            }

            case ReportType.CurrencyTransaction:
            {
                var rows = await _context.Transactions.AsNoTracking()
                    .Where(t => t.CreatedAt >= from && t.CreatedAt < to && t.Status == TransactionStatus.Completed
                                && (t.Type == TransactionType.Deposit || t.Type == TransactionType.Withdrawal))
                    .Select(t => new { t.AccountId, t.Currency, Day = t.CreatedAt.Date, Amount = t.Amount < 0 ? -t.Amount : t.Amount })
                    .ToListAsync();

                var accountNumbers = await _context.Accounts.IgnoreQueryFilters().AsNoTracking()
                    .Select(a => new { a.Id, a.AccountNumber }).ToDictionaryAsync(a => a.Id, a => a.AccountNumber);

                var items = rows
                    .GroupBy(r => new { r.AccountId, r.Currency, r.Day })
                    .Select(g => new { g.Key.AccountId, g.Key.Currency, g.Key.Day, Total = g.Sum(r => r.Amount), Count = g.Count() })
                    .Where(g => g.Total > ReportingThreshold)
                    .OrderBy(g => g.Day)
                    .Select(g => new
                    {
                        accountId = g.AccountId,
                        accountNumber = accountNumbers.GetValueOrDefault(g.AccountId),
                        currency = g.Currency,
                        day = g.Day,
                        total = g.Total,
                        transactionCount = g.Count
                    })
                    .ToList();

                count = items.Count;
                payload = new { type = "CTR", threshold = ReportingThreshold, from, to = to.AddDays(-1), items };
                break;
            }

            default:
            {
                var txQuery = _context.Transactions.AsNoTracking()
                    .Where(t => t.CreatedAt >= from && t.CreatedAt < to && t.Status == TransactionStatus.Completed);

                var txCount = await txQuery.CountAsync();
                var volume = await txQuery.SumAsync(t => t.Amount < 0 ? -t.Amount : t.Amount);

                var alertGroups = await _context.ComplianceAlerts.AsNoTracking()
                    .Where(a => a.CreatedAt >= from && a.CreatedAt < to)
                    .GroupBy(a => new { a.AlertType, a.Severity, a.Status })
                    .Select(g => new { g.Key.AlertType, g.Key.Severity, Status = g.Key.Status.ToString(), Count = g.Count() })
                    .ToListAsync();

                var kycGroups = await _context.Users.AsNoTracking()
                    .GroupBy(u => u.KycStatus)
                    .Select(g => new { Status = g.Key.ToString(), Count = g.Count() })
                    .ToListAsync();

                var chargebacks = await _context.Chargebacks.AsNoTracking().CountAsync(c => c.OpenedAt >= from && c.OpenedAt < to);

                count = alertGroups.Sum(a => a.Count);
                payload = new
                {
                    type = "MonthlyCompliance",
                    from,
                    to = to.AddDays(-1),
                    transactions = txCount,
                    volume,
                    alerts = alertGroups,
                    kycStatus = kycGroups,
                    chargebacksOpened = chargebacks,
                    activeSanctionsEntries = await _context.SanctionedEntities.CountAsync(s => s.IsActive)
                };
                break;
            }
        }

        var report = new RegulatoryReport
        {
            Id = Guid.NewGuid(),
            Type = type,
            PeriodStart = from,
            PeriodEnd = to.AddDays(-1),
            ItemCount = count,
            PayloadJson = JsonSerializer.Serialize(payload),
            GeneratedByUserId = generatedByUserId,
            GeneratedAt = DateTime.UtcNow
        };

        _context.RegulatoryReports.Add(report);
        await _context.SaveChangesAsync();
        await _audit.LogAsync("Compliance.ReportGenerated", generatedByUserId, "RegulatoryReport", report.Id.ToString(),
            $"{type} {from:yyyy-MM-dd}..{to.AddDays(-1):yyyy-MM-dd}, {count} items");
        return report;
    }

    // ------------------------------------------------------------------ helpers

    private static ComplianceAlert NewAlert(Guid accountId, Guid? transactionId, string type, string severity, int risk, string description) => new()
    {
        Id = Guid.NewGuid(),
        AccountId = accountId,
        TransactionId = transactionId,
        AlertType = type,
        Severity = severity,
        RiskScore = risk,
        Description = description,
        Status = AlertStatus.Open,
        CreatedAt = DateTime.UtcNow
    };

    private Task<bool> AlertExistsTodayAsync(Guid accountId, string type)
    {
        var today = DateTime.UtcNow.Date;
        return _context.ComplianceAlerts.AsNoTracking()
            .AnyAsync(a => a.AccountId == accountId && a.AlertType == type && a.CreatedAt >= today);
    }

    private static int CalculateComplianceScore(List<Transaction> transactions, List<ComplianceAlert> alerts)
    {
        var score = 100;
        score -= transactions.Count(t => t.IsFraudulent) * 10;
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

    /// <summary>0-100 risk score from alerts in the last 30 days (higher is riskier).</summary>
    public int RiskScore { get; set; }
}
