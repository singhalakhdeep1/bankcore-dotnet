using System.Text.Json;
using BankCore.Services.CapacityEstimation;
using BankCore.Services.Sharding;
using BankCore.Services.SystemConstraints;
using BankingAPI.Data;
using BankingAPI.DTOs;
using BankingAPI.Extensions;
using BankingAPI.Middleware;
using BankingAPI.Models;
using BankingAPI.Services;
using BankingAPI.Services.Security;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace BankingAPI.Controllers;

// Back-office controllers. Callers authenticate with a JWT or an X-Api-Key, need the stated role, and (if configured)
// must come from an allowed network (Security:AdminAllowedIps).
//   Staff    - Staff, Manager, Admin    : read, review, generate reports
//   Manager  - Manager, Admin           : freeze accounts, resolve disputes, close periods, return transfers
//   Admin    - Admin                    : roles, sanctions list, API keys

public static class AdminAuth
{
    public const string Schemes = "Bearer," + ApiKeyDefaults.Scheme;
}

/// <summary>Users, roles and KYC review.</summary>
[ApiController]
[Route("api/admin/users")]
[Authorize(AuthenticationSchemes = AdminAuth.Schemes, Policy = "Staff")]
[AdminIpAllowList]
public class AdminUsersController : ControllerBase
{
    private readonly IAdminService _admin;
    private readonly IKycService _kyc;

    public AdminUsersController(IAdminService admin, IKycService kyc)
    {
        _admin = admin;
        _kyc = kyc;
    }

    [HttpGet]
    public async Task<IActionResult> List([FromQuery] string? search, [FromQuery] int page = 1, [FromQuery] int pageSize = 25) =>
        Ok(await _admin.ListUsersAsync(search, page, pageSize));

    /// <summary>Assigns a role. Signed-in administrators only (API keys cannot do this).</summary>
    [HttpPut("{id:guid}/role")]
    [Authorize(AuthenticationSchemes = AdminAuth.Schemes, Policy = "InteractiveAdmin")]
    public async Task<IActionResult> SetRole(Guid id, [FromBody] SetRoleRequest request)
    {
        var (ok, error) = await _admin.SetRoleAsync(id, request.Role, User.GetUserId()!.Value);
        return ok ? NoContent() : BadRequest(new { message = error });
    }

    [HttpPost("{id:guid}/lock")]
    [Authorize(AuthenticationSchemes = AdminAuth.Schemes, Policy = "Manager")]
    public Task<IActionResult> Lock(Guid id) => SetLocked(id, true);

    [HttpPost("{id:guid}/unlock")]
    [Authorize(AuthenticationSchemes = AdminAuth.Schemes, Policy = "Manager")]
    public Task<IActionResult> Unlock(Guid id) => SetLocked(id, false);

    private async Task<IActionResult> SetLocked(Guid id, bool locked)
    {
        var (ok, error) = await _admin.SetLockedAsync(id, locked, User.GetUserId()!.Value);
        return ok ? NoContent() : BadRequest(new { message = error });
    }

    /// <summary>Sets a customer's KYC status directly (Pending, Verified or Rejected).</summary>
    [HttpPut("{id:guid}/kyc")]
    public async Task<IActionResult> SetKyc(Guid id, [FromBody] SetKycRequest request)
    {
        var (ok, error) = await _kyc.SetUserStatusAsync(id, request.Status, request.Notes, User.GetUserId()!.Value);
        return ok ? NoContent() : BadRequest(new { message = error });
    }

    [HttpGet("kyc/pending")]
    public async Task<IActionResult> PendingDocuments()
    {
        var documents = await _kyc.ListPendingAsync();
        return Ok(documents.Select(d => new
        {
            d.Id,
            d.UserId,
            userEmail = d.User.Email,
            documentType = d.DocumentType.ToString(),
            fileName = d.OriginalFileName,
            d.ContentType,
            d.SizeBytes,
            d.UploadedAt
        }));
    }

    [HttpGet("kyc/documents/{id:guid}/file")]
    public async Task<IActionResult> DownloadDocument(Guid id)
    {
        var (stream, document) = await _kyc.OpenAsync(id);
        if (stream == null || document == null) return NotFound(new { message = "Document not found" });

        return File(stream, document.ContentType, document.OriginalFileName);
    }

    [HttpPost("kyc/documents/{id:guid}/review")]
    public async Task<IActionResult> ReviewDocument(Guid id, [FromBody] ReviewDocumentRequest request)
    {
        var (ok, error) = await _kyc.ReviewDocumentAsync(id, request.Approve, request.Notes, User.GetUserId()!.Value);
        return ok ? NoContent() : BadRequest(new { message = error });
    }
}

/// <summary>Accounts: search and freeze.</summary>
[ApiController]
[Route("api/admin/accounts")]
[Authorize(AuthenticationSchemes = AdminAuth.Schemes, Policy = "Staff")]
[AdminIpAllowList]
public class AdminAccountsController : ControllerBase
{
    private readonly IAdminService _admin;

    public AdminAccountsController(IAdminService admin)
    {
        _admin = admin;
    }

    [HttpGet]
    public async Task<IActionResult> List([FromQuery] string? search, [FromQuery] AccountStatus? status, [FromQuery] int page = 1, [FromQuery] int pageSize = 25) =>
        Ok(await _admin.ListAccountsAsync(search, status, page, pageSize));

    /// <summary>Freezes, deactivates or reactivates an account. Managers and administrators.</summary>
    [HttpPut("{id:guid}/status")]
    [Authorize(AuthenticationSchemes = AdminAuth.Schemes, Policy = "Manager")]
    public async Task<IActionResult> SetStatus(Guid id, [FromBody] SetAccountStatusRequest request)
    {
        var (ok, error) = await _admin.SetAccountStatusAsync(id, request.Status, User.GetUserId()!.Value);
        return ok ? NoContent() : BadRequest(new { message = error });
    }
}

/// <summary>Alerts, risk, sanctions screening and regulatory reports (SAR, CTR, monthly summary).</summary>
[ApiController]
[Route("api/admin/compliance")]
[Authorize(AuthenticationSchemes = AdminAuth.Schemes, Policy = "Staff")]
[AdminIpAllowList]
public class AdminComplianceController : ControllerBase
{
    private readonly IComplianceService _compliance;
    private readonly ISanctionsService _sanctions;
    private readonly ApplicationDbContext _context;
    private readonly IReportingMirror _mirror;

    public AdminComplianceController(IComplianceService compliance, ISanctionsService sanctions, ApplicationDbContext context, IReportingMirror mirror)
    {
        _compliance = compliance;
        _sanctions = sanctions;
        _context = context;
        _mirror = mirror;
    }

    [HttpGet("alerts")]
    public async Task<IActionResult> Alerts([FromQuery] AlertStatus? status, [FromQuery] string? severity, [FromQuery] int page = 1, [FromQuery] int pageSize = 25) =>
        Ok(await _compliance.ListAlertsAsync(status, severity, page, pageSize));

    [HttpPut("alerts/{id:guid}")]
    public async Task<IActionResult> ReviewAlert(Guid id, [FromBody] ReviewAlertRequest request)
    {
        var alert = await _compliance.ReviewAlertAsync(id, request.Status, request.Notes, User.GetUserId()!.Value);
        return alert == null ? NotFound(new { message = "Alert not found" }) : Ok(alert);
    }

    [HttpGet("risk/{accountId:guid}")]
    public async Task<IActionResult> Risk(Guid accountId) =>
        Ok(new { accountId, riskScore = await _compliance.GetRiskScoreAsync(accountId), compliant = await _compliance.IsAccountCompliantAsync(accountId) });

    /// <summary>Generates and stores a regulatory report for a date range. Pass type SuspiciousActivity, CurrencyTransaction or MonthlyCompliance.</summary>
    [HttpPost("reports")]
    public async Task<IActionResult> GenerateReport([FromBody] GenerateReportRequest request)
    {
        if (request.To < request.From) return BadRequest(new { message = "The end date must not be before the start date." });

        var report = await _compliance.GenerateRegulatoryReportAsync(request.Type, request.From, request.To, User.GetUserId()!.Value);
        await _mirror.MirrorRegulatoryReportAsync(report);
        return StatusCode(StatusCodes.Status201Created, ToDto(report, includePayload: true));
    }

    [HttpGet("reports")]
    public async Task<IActionResult> Reports([FromQuery] ReportType? type)
    {
        var query = _context.RegulatoryReports.AsNoTracking().AsQueryable();
        if (type.HasValue) query = query.Where(r => r.Type == type.Value);

        var reports = await query.OrderByDescending(r => r.GeneratedAt).Take(100).ToListAsync();
        return Ok(reports.Select(r => ToDto(r, includePayload: false)));
    }

    [HttpGet("reports/{id:guid}")]
    public async Task<IActionResult> GetReport(Guid id)
    {
        var report = await _context.RegulatoryReports.AsNoTracking().FirstOrDefaultAsync(r => r.Id == id);
        return report == null ? NotFound(new { message = "Report not found" }) : Ok(ToDto(report, includePayload: true));
    }

    [HttpGet("sanctions")]
    public async Task<IActionResult> Sanctions() => Ok(await _sanctions.ListAsync());

    /// <summary>Screens a name and/or account number against the watch list.</summary>
    [HttpPost("sanctions/screen")]
    public async Task<IActionResult> Screen([FromBody] ScreenRequest request)
    {
        var hit = await _sanctions.ScreenAsync(request.Name, request.AccountNumber);
        return Ok(new { match = hit != null, entry = hit });
    }

    [HttpPost("sanctions")]
    [Authorize(AuthenticationSchemes = AdminAuth.Schemes, Policy = "InteractiveAdmin")]
    public async Task<IActionResult> AddSanction([FromBody] AddSanctionRequest request)
    {
        var entity = await _sanctions.AddAsync(request.Name, request.AccountNumber, request.Country, request.ListName ?? "OFAC-SDN");
        return StatusCode(StatusCodes.Status201Created, entity);
    }

    [HttpDelete("sanctions/{id:guid}")]
    [Authorize(AuthenticationSchemes = AdminAuth.Schemes, Policy = "InteractiveAdmin")]
    public async Task<IActionResult> RemoveSanction(Guid id) =>
        await _sanctions.RemoveAsync(id) ? NoContent() : NotFound(new { message = "Entry not found" });

    private static object ToDto(RegulatoryReport r, bool includePayload) => new
    {
        r.Id,
        type = r.Type.ToString(),
        r.PeriodStart,
        r.PeriodEnd,
        r.ItemCount,
        r.GeneratedAt,
        r.GeneratedByUserId,
        payload = includePayload ? JsonSerializer.Deserialize<JsonElement>(r.PayloadJson) : (JsonElement?)null
    };
}

/// <summary>Trial balance, balance sheet, period closing and ledger reconciliation.</summary>
[ApiController]
[Route("api/admin/ledger")]
[Authorize(AuthenticationSchemes = AdminAuth.Schemes, Policy = "Staff")]
[AdminIpAllowList]
public class AdminLedgerController : ControllerBase
{
    private readonly ILedgerService _ledger;

    public AdminLedgerController(ILedgerService ledger)
    {
        _ledger = ledger;
    }

    [HttpGet("trial-balance")]
    public async Task<IActionResult> TrialBalance([FromQuery] DateTime? asOf) =>
        Ok(new { trial = await _ledger.GetTrialBalanceAsync(asOf.AsUtc()), });

    [HttpGet("balance-sheet")]
    public async Task<IActionResult> BalanceSheet([FromQuery] DateTime? asOf) =>
        Ok(await _ledger.GetBalanceSheetAsync(asOf.AsUtc()));

    /// <summary>Checks every account's stored balance against its ledger entries.</summary>
    [HttpGet("reconcile")]
    public async Task<IActionResult> Reconcile() => Ok(await _ledger.ReconcileAllAsync());

    [HttpGet("periods")]
    public async Task<IActionResult> Periods() => Ok(await _ledger.GetPeriodsAsync());

    /// <summary>Closes a finished accounting period and snapshots every account's closing balance.</summary>
    [HttpPost("periods/close")]
    [Authorize(AuthenticationSchemes = AdminAuth.Schemes, Policy = "Manager")]
    public async Task<IActionResult> ClosePeriod([FromBody] ClosePeriodRequest request)
    {
        try
        {
            var period = await _ledger.ClosePeriodAsync(request.Name, request.Start, request.End, User.GetUserId()!.Value);
            return StatusCode(StatusCodes.Status201Created, new
            {
                period.Id, period.Name, period.StartDate, period.EndDate, status = period.Status.ToString(),
                period.TotalDebits, period.TotalCredits, accounts = period.Balances.Count, period.ClosedAt
            });
        }
        catch (InvalidOperationException ex)
        {
            return BadRequest(new { message = ex.Message });
        }
    }
}

/// <summary>Payment analytics, reconciliation, chargebacks, bank transfers and manual job runs.</summary>
[ApiController]
[Route("api/admin/payments")]
[Authorize(AuthenticationSchemes = AdminAuth.Schemes, Policy = "Staff")]
[AdminIpAllowList]
public class AdminPaymentsController : ControllerBase
{
    private readonly IPaymentAnalyticsService _analytics;
    private readonly IChargebackService _chargebacks;
    private readonly IBankTransferService _transfers;
    private readonly IRecurringPaymentService _recurring;

    public AdminPaymentsController(IPaymentAnalyticsService analytics, IChargebackService chargebacks, IBankTransferService transfers, IRecurringPaymentService recurring)
    {
        _analytics = analytics;
        _chargebacks = chargebacks;
        _transfers = transfers;
        _recurring = recurring;
    }

    [HttpGet("analytics")]
    public async Task<IActionResult> Analytics([FromQuery] DateTime? from, [FromQuery] DateTime? to) =>
        Ok(await _analytics.GetAnalyticsAsync((from ?? DateTime.UtcNow.AddDays(-30)).AsUtc(), (to ?? DateTime.UtcNow).AsUtc()));

    /// <summary>Checks completed payments against the ledger and flags payments stuck in processing.</summary>
    [HttpGet("reconciliation")]
    public async Task<IActionResult> Reconciliation() => Ok(await _analytics.ReconcileAsync());

    [HttpGet("chargebacks")]
    public async Task<IActionResult> Chargebacks([FromQuery] ChargebackStatus? status)
    {
        var items = await _chargebacks.ListAllAsync(status);
        return Ok(items.Select(c => ChargebackDto.From(c, c.PaymentLog.TransactionId)));
    }

    [HttpPost("chargebacks/{id:guid}/review")]
    public async Task<IActionResult> StartReview(Guid id)
    {
        var (ok, error) = await _chargebacks.StartReviewAsync(id, User.GetUserId()!.Value);
        return ok ? NoContent() : BadRequest(new { message = error });
    }

    /// <summary>Resolves a dispute. CustomerWon credits the customer; MerchantWon rejects the dispute.</summary>
    [HttpPost("chargebacks/{id:guid}/resolve")]
    [Authorize(AuthenticationSchemes = AdminAuth.Schemes, Policy = "Manager")]
    public async Task<IActionResult> Resolve(Guid id, [FromBody] ResolveChargebackRequest request)
    {
        var (ok, error) = await _chargebacks.ResolveAsync(id, request.Outcome, request.Notes, User.GetUserId()!.Value);
        return ok ? NoContent() : BadRequest(new { message = error });
    }

    [HttpGet("bank-transfers")]
    public async Task<IActionResult> BankTransfers([FromQuery] BankTransferStatus? status, [FromQuery] int take = 100) =>
        Ok((await _transfers.ListAllAsync(status, take)).Select(BankTransferDto.From));

    /// <summary>Marks a transfer as returned by the beneficiary bank and credits the customer (fee is kept).</summary>
    [HttpPost("bank-transfers/{id:guid}/return")]
    [Authorize(AuthenticationSchemes = AdminAuth.Schemes, Policy = "Manager")]
    public async Task<IActionResult> ReturnTransfer(Guid id, [FromBody] ReturnTransferRequest request)
    {
        var (ok, error) = await _transfers.ReturnAsync(id, request.Reason, User.GetUserId()!.Value);
        return ok ? NoContent() : BadRequest(new { message = error });
    }

    /// <summary>Runs the settlement job now instead of waiting for the schedule.</summary>
    [HttpPost("jobs/settlement/run")]
    [Authorize(AuthenticationSchemes = AdminAuth.Schemes, Policy = "Manager")]
    public async Task<IActionResult> RunSettlement() => Ok(new { advanced = await _transfers.SettleDueAsync() });

    /// <summary>Runs due recurring payments now instead of waiting for the schedule.</summary>
    [HttpPost("jobs/recurring/run")]
    [Authorize(AuthenticationSchemes = AdminAuth.Schemes, Policy = "Manager")]
    public async Task<IActionResult> RunRecurring() => Ok(new { attempted = await _recurring.ProcessDueAsync() });
}

/// <summary>Audit log, API keys and system diagnostics (capacity, constraints, shards, databases).</summary>
[ApiController]
[Route("api/admin/system")]
[Authorize(AuthenticationSchemes = AdminAuth.Schemes, Policy = "Staff")]
[AdminIpAllowList]
public class AdminSystemController : ControllerBase
{
    private readonly IAdminService _admin;
    private readonly IApiKeyService _keys;
    private readonly IReportingMirror _mirror;
    private readonly ApplicationDbContext _context;
    private readonly CapacityEstimationService _capacity;
    private readonly SystemConstraintsAnalysisService _constraints;
    private readonly DatabaseShardingService _sharding;

    public AdminSystemController(
        IAdminService admin,
        IApiKeyService keys,
        IReportingMirror mirror,
        ApplicationDbContext context,
        CapacityEstimationService capacity,
        SystemConstraintsAnalysisService constraints,
        DatabaseShardingService sharding)
    {
        _admin = admin;
        _keys = keys;
        _mirror = mirror;
        _context = context;
        _capacity = capacity;
        _constraints = constraints;
        _sharding = sharding;
    }

    [HttpGet("audit")]
    public async Task<IActionResult> Audit([FromQuery] string? action, [FromQuery] Guid? userId, [FromQuery] int page = 1, [FromQuery] int pageSize = 50) =>
        Ok(await _admin.ListAuditAsync(action, userId, page, pageSize));

    /// <summary>Connectivity of the optional MySQL, SQL Server, MongoDB and RavenDB integrations.</summary>
    [HttpGet("databases")]
    public async Task<IActionResult> Databases() => Ok(await _mirror.CheckDatabasesAsync());

    /// <summary>Capacity estimate (servers, storage, cache) for the current load, using the live account and transaction counts.</summary>
    [HttpGet("capacity")]
    public async Task<IActionResult> Capacity()
    {
        var since = DateTime.UtcNow.AddDays(-1);
        var daily = await _context.Transactions.AsNoTracking().CountAsync(t => t.CreatedAt >= since);

        var volume = new TransactionVolume
        {
            DailyTransactions = Math.Max(daily, 1),
            AccountCount = await _context.Accounts.CountAsync(),
            UserCount = await _context.Users.CountAsync(),
            PeakHourTransactions = Math.Max(daily / 4, 1)
        };

        return Ok(new { volume, report = _capacity.GenerateCapacityReport(volume) });
    }

    [HttpGet("constraints")]
    public IActionResult Constraints([FromQuery] int targetTps = 1000, [FromQuery] double targetLatencyMs = 200) =>
        Ok(_constraints.AnalyzeConstraints(new SystemRequirements { TargetTPS = targetTps, TargetLatencyMs = targetLatencyMs }));

    /// <summary>Which shard an account number maps to, plus shard health.</summary>
    [HttpGet("shards")]
    public async Task<IActionResult> Shards([FromQuery] string? accountNumber) => Ok(new
    {
        shardForAccount = string.IsNullOrWhiteSpace(accountNumber) ? null : _sharding.GetShardIdByAccount(accountNumber),
        health = await _sharding.GetShardHealthAsync()
    });

    // ---- API keys (administrators only)

    [HttpGet("api-keys")]
    [Authorize(AuthenticationSchemes = AdminAuth.Schemes, Policy = "InteractiveAdmin")]
    public async Task<IActionResult> ListKeys() =>
        Ok((await _keys.ListAsync()).Select(k => new { k.Id, k.Name, k.Prefix, k.Scopes, k.OwnerUserId, k.CreatedAt, k.ExpiresAt, k.RevokedAt, k.LastUsedAt, active = k.IsActive }));

    /// <summary>Creates an API key for the calling administrator. The key is shown once; store it safely.</summary>
    [HttpPost("api-keys")]
    [Authorize(AuthenticationSchemes = AdminAuth.Schemes, Policy = "InteractiveAdmin")]
    public async Task<IActionResult> CreateKey([FromBody] CreateApiKeyRequest request)
    {
        var (key, plain) = await _keys.CreateAsync(User.GetUserId()!.Value, request.Name, request.Scopes ?? "read", request.ExpiresInDays);
        return StatusCode(StatusCodes.Status201Created, new { key.Id, key.Name, key.Prefix, key.Scopes, key.ExpiresAt, apiKey = plain });
    }

    [HttpDelete("api-keys/{id:guid}")]
    [Authorize(AuthenticationSchemes = AdminAuth.Schemes, Policy = "InteractiveAdmin")]
    public async Task<IActionResult> RevokeKey(Guid id) =>
        await _keys.RevokeAsync(id) ? NoContent() : NotFound(new { message = "API key not found" });
}

// ---- request bodies

public class SetRoleRequest { public UserRole Role { get; set; } }
public class SetKycRequest { public KycStatus Status { get; set; } public string? Notes { get; set; } }
public class ReviewDocumentRequest { public bool Approve { get; set; } public string? Notes { get; set; } }
public class SetAccountStatusRequest { public AccountStatus Status { get; set; } }
public class ReviewAlertRequest { public AlertStatus Status { get; set; } public string? Notes { get; set; } }
public class GenerateReportRequest { public ReportType Type { get; set; } public DateTime From { get; set; } public DateTime To { get; set; } }
public class ScreenRequest { public string? Name { get; set; } public string? AccountNumber { get; set; } }
public class AddSanctionRequest { public string Name { get; set; } = string.Empty; public string? AccountNumber { get; set; } public string? Country { get; set; } public string? ListName { get; set; } }
public class ClosePeriodRequest { public string Name { get; set; } = string.Empty; public DateTime Start { get; set; } public DateTime End { get; set; } }
public class ReturnTransferRequest { public string Reason { get; set; } = "Returned by beneficiary bank"; }
public class CreateApiKeyRequest { public string Name { get; set; } = string.Empty; public string? Scopes { get; set; } public int? ExpiresInDays { get; set; } }
