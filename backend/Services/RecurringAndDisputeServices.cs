using BankingAPI.Data;
using BankingAPI.DTOs;
using BankingAPI.Extensions;
using BankingAPI.Models;
using BankingAPI.Services.Security;
using Microsoft.EntityFrameworkCore;

namespace BankingAPI.Services;

// ============================================================================ recurring payments

public interface IRecurringPaymentService
{
    Task<(RecurringPayment? Payment, string? Error)> CreateAsync(Guid userId, CreateRecurringPaymentRequest request);
    Task<List<RecurringPayment>> ListAsync(Guid userId);
    Task<bool> SetStatusAsync(Guid userId, Guid id, RecurringStatus status);

    /// <summary>Runs every payment that is due. Returns the number of payments attempted.</summary>
    Task<int> ProcessDueAsync();
}

/// <summary>Standing instructions to pay a merchant daily, weekly or monthly.</summary>
public class RecurringPaymentService : IRecurringPaymentService
{
    private const int MaxConsecutiveFailures = 3;

    private readonly ApplicationDbContext _context;
    private readonly IPaymentGatewayService _payments;
    private readonly ISanctionsService _sanctions;
    private readonly IAuditService _audit;
    private readonly ILogger<RecurringPaymentService> _logger;

    public RecurringPaymentService(
        ApplicationDbContext context,
        IPaymentGatewayService payments,
        ISanctionsService sanctions,
        IAuditService audit,
        ILogger<RecurringPaymentService> logger)
    {
        _context = context;
        _payments = payments;
        _sanctions = sanctions;
        _audit = audit;
        _logger = logger;
    }

    public async Task<(RecurringPayment? Payment, string? Error)> CreateAsync(Guid userId, CreateRecurringPaymentRequest request)
    {
        var account = await _context.Accounts.AsNoTracking().FirstOrDefaultAsync(a => a.Id == request.AccountId && a.UserId == userId);
        if (account == null) return (null, "Account not found");
        if (account.Status != AccountStatus.Active) return (null, "Account is not active");
        if (decimal.Round(request.Amount, 2) != request.Amount) return (null, "Amount must have at most 2 decimals");

        var start = (request.StartAt ?? DateTime.UtcNow).AsUtc();
        if (request.EndAt.HasValue && request.EndAt.Value <= start) return (null, "The end date must be after the first payment");

        if (await _sanctions.ScreenAsync(request.MerchantName) != null)
        {
            return (null, "This merchant cannot be paid because it is on a sanctions list.");
        }

        var payment = new RecurringPayment
        {
            Id = Guid.NewGuid(),
            AccountId = account.Id,
            UserId = userId,
            MerchantName = request.MerchantName.Trim(),
            Amount = request.Amount,
            Currency = account.Currency,
            Frequency = request.Frequency,
            NextRunAt = start,
            EndAt = request.EndAt.AsUtc()
        };

        _context.RecurringPayments.Add(payment);
        await _context.SaveChangesAsync();
        await _audit.LogAsync("RecurringPayment.Created", userId, "RecurringPayment", payment.Id.ToString(),
            $"{payment.Frequency} {payment.Amount:N2} to {payment.MerchantName}");
        return (payment, null);
    }

    public async Task<List<RecurringPayment>> ListAsync(Guid userId) =>
        await _context.RecurringPayments.AsNoTracking()
            .Where(p => p.UserId == userId)
            .OrderByDescending(p => p.CreatedAt)
            .ToListAsync();

    public async Task<bool> SetStatusAsync(Guid userId, Guid id, RecurringStatus status)
    {
        var payment = await _context.RecurringPayments.FirstOrDefaultAsync(p => p.Id == id && p.UserId == userId);
        if (payment == null || payment.Status == RecurringStatus.Cancelled) return false;

        payment.Status = status;
        if (status == RecurringStatus.Active) payment.ConsecutiveFailures = 0;
        await _context.SaveChangesAsync();
        await _audit.LogAsync($"RecurringPayment.{status}", userId, "RecurringPayment", id.ToString());
        return true;
    }

    public async Task<int> ProcessDueAsync()
    {
        var now = DateTime.UtcNow;
        var dueIds = await _context.RecurringPayments.AsNoTracking()
            .Where(p => p.Status == RecurringStatus.Active && p.NextRunAt <= now)
            .Select(p => p.Id)
            .ToListAsync();

        var attempted = 0;
        foreach (var id in dueIds)
        {
            var payment = await _context.RecurringPayments.FirstAsync(p => p.Id == id);

            if (payment.EndAt.HasValue && payment.EndAt.Value < now)
            {
                payment.Status = RecurringStatus.Cancelled;
                payment.LastResult = "Ended";
                await _context.SaveChangesAsync();
                continue;
            }

            // Move the schedule forward first: if anything below fails midway, a payment is skipped, never charged twice.
            payment.NextRunAt = NextOccurrence(payment.NextRunAt, payment.Frequency, now);
            await _context.SaveChangesAsync();

            attempted++;
            try
            {
                var result = await _payments.ProcessPaymentAsync(new PaymentRequest
                {
                    AccountId = payment.AccountId,
                    UserId = payment.UserId,
                    Amount = payment.Amount,
                    MerchantName = payment.MerchantName,
                    Description = $"Recurring payment to {payment.MerchantName}"
                });

                payment.LastRunAt = now;
                if (result.Success)
                {
                    payment.ConsecutiveFailures = 0;
                    payment.LastResult = $"Paid {result.TransactionId}";
                }
                else
                {
                    payment.ConsecutiveFailures++;
                    payment.LastResult = result.ErrorMessage;
                    if (payment.ConsecutiveFailures >= MaxConsecutiveFailures) payment.Status = RecurringStatus.Failed;
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Recurring payment {Id} failed", id);
                payment.ConsecutiveFailures++;
                payment.LastResult = "Processing error";
                if (payment.ConsecutiveFailures >= MaxConsecutiveFailures) payment.Status = RecurringStatus.Failed;
            }

            await _context.SaveChangesAsync();
        }

        return attempted;
    }

    /// <summary>The first scheduled date after <paramref name="now"/>, skipping any missed cycles.</summary>
    public static DateTime NextOccurrence(DateTime scheduled, RecurrenceFrequency frequency, DateTime now)
    {
        var next = scheduled;
        do
        {
            next = frequency switch
            {
                RecurrenceFrequency.Daily => next.AddDays(1),
                RecurrenceFrequency.Weekly => next.AddDays(7),
                _ => next.AddMonths(1)
            };
        }
        while (next <= now);

        return next;
    }
}

// ============================================================================ chargebacks

public interface IChargebackService
{
    Task<(Chargeback? Chargeback, string? Error)> OpenAsync(Guid userId, string paymentReference, string reason);
    Task<List<Chargeback>> ListForUserAsync(Guid userId);
    Task<List<Chargeback>> ListAllAsync(ChargebackStatus? status);
    Task<(bool Ok, string? Error)> StartReviewAsync(Guid id, Guid staffUserId);
    Task<(bool Ok, string? Error)> ResolveAsync(Guid id, ChargebackStatus outcome, string? notes, Guid staffUserId);
}

/// <summary>A customer disputes a merchant payment; staff review it and either credit the customer back or reject the dispute.</summary>
public class ChargebackService : IChargebackService
{
    public const int DisputeWindowDays = 120;

    private readonly ApplicationDbContext _context;
    private readonly ITransactionService _transactions;
    private readonly IAuditService _audit;

    public ChargebackService(ApplicationDbContext context, ITransactionService transactions, IAuditService audit)
    {
        _context = context;
        _transactions = transactions;
        _audit = audit;
    }

    public async Task<(Chargeback? Chargeback, string? Error)> OpenAsync(Guid userId, string paymentReference, string reason)
    {
        var payment = await _context.PaymentLogs
            .Include(p => p.Account)
            .FirstOrDefaultAsync(p => p.TransactionId == paymentReference && p.Account.UserId == userId);

        if (payment == null) return (null, "Payment not found");
        if (payment.Kind != "MerchantPayment") return (null, "Only merchant payments can be disputed.");
        if (payment.Status != "Completed") return (null, $"A payment with status '{payment.Status}' cannot be disputed.");
        if (payment.CreatedAt < DateTime.UtcNow.AddDays(-DisputeWindowDays))
            return (null, $"Payments can only be disputed within {DisputeWindowDays} days.");

        var chargeback = new Chargeback
        {
            Id = Guid.NewGuid(),
            PaymentLogId = payment.Id,
            AccountId = payment.AccountId,
            Amount = payment.Amount,
            Reason = reason.Trim()
        };

        payment.Status = "Disputed";
        payment.UpdatedAt = DateTime.UtcNow;
        _context.Chargebacks.Add(chargeback);
        await _context.SaveChangesAsync();
        await _audit.LogAsync("Chargeback.Opened", userId, "Chargeback", chargeback.Id.ToString(), $"Payment {paymentReference}: {reason}");
        return (chargeback, null);
    }

    public async Task<List<Chargeback>> ListForUserAsync(Guid userId) =>
        await _context.Chargebacks.AsNoTracking()
            .Include(c => c.PaymentLog)
            .Where(c => c.Account.UserId == userId)
            .OrderByDescending(c => c.OpenedAt)
            .ToListAsync();

    public async Task<List<Chargeback>> ListAllAsync(ChargebackStatus? status)
    {
        var query = _context.Chargebacks.AsNoTracking().Include(c => c.PaymentLog).AsQueryable();
        if (status.HasValue) query = query.Where(c => c.Status == status.Value);
        return await query.OrderByDescending(c => c.OpenedAt).Take(200).ToListAsync();
    }

    public async Task<(bool Ok, string? Error)> StartReviewAsync(Guid id, Guid staffUserId)
    {
        var chargeback = await _context.Chargebacks.FirstOrDefaultAsync(c => c.Id == id);
        if (chargeback == null) return (false, "Chargeback not found");
        if (chargeback.Status != ChargebackStatus.Opened) return (false, $"Chargeback is already {chargeback.Status}");

        chargeback.Status = ChargebackStatus.UnderReview;
        await _context.SaveChangesAsync();
        await _audit.LogAsync("Chargeback.UnderReview", staffUserId, "Chargeback", id.ToString());
        return (true, null);
    }

    public async Task<(bool Ok, string? Error)> ResolveAsync(Guid id, ChargebackStatus outcome, string? notes, Guid staffUserId)
    {
        if (outcome is not (ChargebackStatus.CustomerWon or ChargebackStatus.MerchantWon))
        {
            return (false, "The outcome must be CustomerWon or MerchantWon.");
        }

        var chargeback = await _context.Chargebacks.Include(c => c.PaymentLog).FirstOrDefaultAsync(c => c.Id == id);
        if (chargeback == null) return (false, "Chargeback not found");
        if (chargeback.Status is ChargebackStatus.CustomerWon or ChargebackStatus.MerchantWon)
            return (false, $"Chargeback is already resolved ({chargeback.Status})");

        if (outcome == ChargebackStatus.CustomerWon)
        {
            var credit = await _transactions.ProcessRefundAsync(chargeback.AccountId, chargeback.Amount, $"chargeback {chargeback.Id}");
            if (credit == null) return (false, "The customer could not be credited (is the account closed?)");

            chargeback.PaymentLog.Status = "ChargedBack";
            chargeback.PaymentLog.RefundAmount = chargeback.Amount;
        }
        else
        {
            chargeback.PaymentLog.Status = "Completed";
        }

        chargeback.PaymentLog.UpdatedAt = DateTime.UtcNow;
        chargeback.Status = outcome;
        chargeback.ResolvedByUserId = staffUserId;
        chargeback.ResolutionNotes = notes;
        chargeback.ResolvedAt = DateTime.UtcNow;

        await _context.SaveChangesAsync();
        await _audit.LogAsync("Chargeback.Resolved", staffUserId, "Chargeback", id.ToString(), $"{outcome}: {notes}");
        return (true, null);
    }
}

// ============================================================================ analytics and reconciliation

public class PaymentAnalytics
{
    public DateTime From { get; set; }
    public DateTime To { get; set; }
    public int TotalPayments { get; set; }
    public int Completed { get; set; }
    public int Failed { get; set; }

    /// <summary>Completed payments as a share of completed + failed (0-1).</summary>
    public double SuccessRate { get; set; }

    public decimal CompletedVolume { get; set; }
    public decimal RefundedVolume { get; set; }
    public decimal GatewayFees { get; set; }
    public int ChargebacksOpened { get; set; }
    public double ChargebackRate { get; set; }
    public Dictionary<string, int> ByStatus { get; set; } = new();
    public List<object> ByProvider { get; set; } = new();
    public List<object> TopMerchants { get; set; } = new();
    public List<object> DailyVolume { get; set; } = new();
}

public class ReconciliationIssue
{
    public string PaymentReference { get; set; } = string.Empty;
    public string Issue { get; set; } = string.Empty;
}

public class PaymentReconciliation
{
    public DateTime RunAt { get; set; } = DateTime.UtcNow;
    public int PaymentsChecked { get; set; }
    public List<ReconciliationIssue> Issues { get; set; } = new();
    public bool Clean => Issues.Count == 0;
}

public interface IPaymentAnalyticsService
{
    Task<PaymentAnalytics> GetAnalyticsAsync(DateTime from, DateTime to);
    Task<PaymentReconciliation> ReconcileAsync();
}

public class PaymentAnalyticsService : IPaymentAnalyticsService
{
    private readonly ApplicationDbContext _context;

    public PaymentAnalyticsService(ApplicationDbContext context)
    {
        _context = context;
    }

    public async Task<PaymentAnalytics> GetAnalyticsAsync(DateTime from, DateTime to)
    {
        var start = DateTime.SpecifyKind(from.Date, DateTimeKind.Utc);
        var end = DateTime.SpecifyKind(to.Date, DateTimeKind.Utc).AddDays(1);

        var logs = await _context.PaymentLogs.AsNoTracking()
            .Where(p => p.CreatedAt >= start && p.CreatedAt < end)
            .ToListAsync();

        var completed = logs.Where(p => p.Status is "Completed" or "Refunded" or "Disputed" or "ChargedBack").ToList();
        var failed = logs.Count(p => p.Status is "Failed" or "Error");
        var chargebacks = await _context.Chargebacks.AsNoTracking().CountAsync(c => c.OpenedAt >= start && c.OpenedAt < end);

        return new PaymentAnalytics
        {
            From = start,
            To = end.AddDays(-1),
            TotalPayments = logs.Count,
            Completed = completed.Count,
            Failed = failed,
            SuccessRate = completed.Count + failed == 0 ? 0 : Math.Round(completed.Count / (double)(completed.Count + failed), 4),
            CompletedVolume = completed.Sum(p => p.Amount),
            RefundedVolume = logs.Sum(p => p.RefundAmount ?? 0),
            GatewayFees = completed.Sum(p => p.Fee),
            ChargebacksOpened = chargebacks,
            ChargebackRate = completed.Count == 0 ? 0 : Math.Round(chargebacks / (double)completed.Count, 4),
            ByStatus = logs.GroupBy(p => p.Status).ToDictionary(g => g.Key, g => g.Count()),
            ByProvider = logs.Where(p => p.Provider != null)
                .GroupBy(p => new { p.Provider, p.ProviderMode })
                .Select(g => (object)new { provider = g.Key.Provider, mode = g.Key.ProviderMode, count = g.Count(), volume = g.Sum(p => p.Amount) })
                .ToList(),
            TopMerchants = completed.Where(p => p.Kind == "MerchantPayment")
                .GroupBy(p => p.MerchantName)
                .Select(g => new { merchant = g.Key, count = g.Count(), volume = g.Sum(p => p.Amount) })
                .OrderByDescending(x => x.volume)
                .Take(10)
                .Select(x => (object)x)
                .ToList(),
            DailyVolume = completed.GroupBy(p => p.CreatedAt.Date).OrderBy(g => g.Key)
                .Select(g => (object)new { day = g.Key, count = g.Count(), volume = g.Sum(p => p.Amount) })
                .ToList()
        };
    }

    /// <summary>Checks every completed payment against the ledger, and finds payments stuck in processing.</summary>
    public async Task<PaymentReconciliation> ReconcileAsync()
    {
        var report = new PaymentReconciliation();

        var logs = await _context.PaymentLogs.AsNoTracking()
            .Where(p => p.Status == "Completed" || p.Status == "Processing")
            .ToListAsync();
        report.PaymentsChecked = logs.Count;

        var txIds = logs.Where(l => l.LedgerTransactionId.HasValue).Select(l => l.LedgerTransactionId!.Value).ToList();
        var transactions = await _context.Transactions.AsNoTracking()
            .Where(t => txIds.Contains(t.Id))
            .ToDictionaryAsync(t => t.Id);

        foreach (var log in logs)
        {
            if (log.Status == "Processing")
            {
                if (log.CreatedAt < DateTime.UtcNow.AddMinutes(-15))
                    report.Issues.Add(new ReconciliationIssue { PaymentReference = log.TransactionId, Issue = "Stuck in Processing for over 15 minutes" });
                continue;
            }

            if (log.LedgerTransactionId == null || !transactions.TryGetValue(log.LedgerTransactionId.Value, out var tx))
            {
                report.Issues.Add(new ReconciliationIssue { PaymentReference = log.TransactionId, Issue = "Completed payment has no ledger transaction" });
                continue;
            }

            var expected = log.Kind == "CardTopUp" ? log.Amount : -log.Amount;
            if (tx.Amount != expected)
                report.Issues.Add(new ReconciliationIssue { PaymentReference = log.TransactionId, Issue = $"Ledger amount {tx.Amount:N2} does not match payment amount {expected:N2}" });

            if (tx.Status != TransactionStatus.Completed)
                report.Issues.Add(new ReconciliationIssue { PaymentReference = log.TransactionId, Issue = $"Ledger transaction is {tx.Status}, payment is Completed" });

            if (log.Kind == "CardTopUp" && string.IsNullOrEmpty(log.GatewayTransactionId))
                report.Issues.Add(new ReconciliationIssue { PaymentReference = log.TransactionId, Issue = "Completed card top-up has no gateway transaction id" });
        }

        return report;
    }
}
