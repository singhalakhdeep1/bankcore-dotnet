using System.Text.RegularExpressions;
using BankingAPI.Data;
using BankingAPI.DTOs;
using BankingAPI.Models;
using BankingAPI.Services.Security;
using Microsoft.EntityFrameworkCore;

namespace BankingAPI.Services;

public interface IBankTransferService
{
    Task<(BankTransfer? Transfer, string? Error)> SubmitAsync(Guid userId, BankTransferRequest request);
    Task<List<BankTransfer>> ListAsync(Guid accountId);
    Task<BankTransfer?> GetAsync(Guid id);
    Task<List<BankTransfer>> ListAllAsync(BankTransferStatus? status, int take);

    /// <summary>Moves due transfers along (Submitted to Processing to Settled). Returns how many changed.</summary>
    Task<int> SettleDueAsync();

    /// <summary>Marks a transfer as returned by the beneficiary bank and credits the amount back (the fee is kept).</summary>
    Task<(bool Ok, string? Error)> ReturnAsync(Guid id, string reason, Guid staffUserId);
}

/// <summary>
/// Outgoing ACH, domestic wire and international SWIFT transfers. Validates the routing details
/// (ABA checksum, IBAN mod-97, BIC format), applies the compliance gate, then debits the amount and fee
/// atomically. A settlement job later clears the in-flight amount against cash in the general ledger.
/// </summary>
public class BankTransferService : IBankTransferService
{
    public const decimal AchFee = 0.50m;
    public const decimal WireFee = 15m;
    public const decimal SwiftBaseFee = 35m;

    private static readonly Regex BicPattern = new("^[A-Z]{4}[A-Z]{2}[A-Z0-9]{2}([A-Z0-9]{3})?$", RegexOptions.Compiled);

    private readonly ApplicationDbContext _context;
    private readonly ITransactionService _transactions;
    private readonly ILedgerService _ledger;
    private readonly IAuditService _audit;

    public BankTransferService(ApplicationDbContext context, ITransactionService transactions, ILedgerService ledger, IAuditService audit)
    {
        _context = context;
        _transactions = transactions;
        _ledger = ledger;
        _audit = audit;
    }

    public async Task<(BankTransfer? Transfer, string? Error)> SubmitAsync(Guid userId, BankTransferRequest request)
    {
        var account = await _context.Accounts.FirstOrDefaultAsync(a => a.Id == request.AccountId && a.UserId == userId);
        if (account == null) return (null, "Account not found");
        if (account.Status != AccountStatus.Active) return (null, "Account is not active");

        var problem = ValidateDetails(request, account.Currency);
        if (problem != null) return (null, problem);

        if (decimal.Round(request.Amount, 2) != request.Amount) return (null, "Amount must have at most 2 decimals");

        var fee = CalculateFee(request.Rail, request.Amount);
        if (account.Balance + account.OverdraftLimit < request.Amount + fee)
        {
            return (null, $"Insufficient funds: the transfer needs {request.Amount + fee:N2} including a {fee:N2} fee");
        }

        var reference = $"{request.Rail.ToString().ToUpperInvariant()}{DateTime.UtcNow:yyyyMMddHHmmss}{Guid.NewGuid().ToString("N")[..8].ToUpperInvariant()}";
        var now = DateTime.UtcNow;

        var transfer = new BankTransfer
        {
            Id = Guid.NewGuid(),
            AccountId = account.Id,
            Rail = request.Rail,
            Amount = request.Amount,
            Fee = fee,
            Currency = account.Currency,
            BeneficiaryName = request.BeneficiaryName.Trim(),
            BeneficiaryAccountNumber = request.BeneficiaryAccountNumber?.Trim(),
            RoutingNumber = request.RoutingNumber?.Trim(),
            SwiftBic = request.SwiftBic?.Trim().ToUpperInvariant(),
            Iban = request.Iban is null ? null : Regex.Replace(request.Iban, @"\s", "").ToUpperInvariant(),
            DestinationCountry = request.DestinationCountry?.Trim().ToUpperInvariant(),
            Reference = reference,
            Status = BankTransferStatus.Submitted,
            SubmittedAt = now,
            ExpectedSettlementAt = ExpectedSettlement(request.Rail, now)
        };

        try
        {
            await using var dbTransaction = _context.Database.IsRelational()
                ? await _context.Database.BeginTransactionAsync()
                : null;

            try
            {
                var debit = await _transactions.ProcessExternalDebitAsync(
                    account.Id, request.Amount, $"{request.Rail} transfer to {transfer.BeneficiaryName}", reference, transfer.BeneficiaryName);
                if (debit == null) return (null, "The transfer was rejected: insufficient funds or daily limit exceeded");
                transfer.DebitTransactionId = debit.Id;

                var feeTx = await _transactions.ProcessFeeAsync(account.Id, fee, $"{request.Rail} transfer fee", $"{reference}-FEE");
                transfer.FeeTransactionId = feeTx?.Id;

                _context.BankTransfers.Add(transfer);
                await _context.SaveChangesAsync();
                if (dbTransaction != null) await dbTransaction.CommitAsync();
            }
            catch
            {
                if (dbTransaction != null) await dbTransaction.RollbackAsync();
                _context.ChangeTracker.Clear();
                throw;
            }
        }
        catch (TransactionRejectedException ex)
        {
            return (null, ex.Message);
        }

        await _audit.LogAsync("BankTransfer.Submitted", userId, "BankTransfer", transfer.Id.ToString(),
            $"{transfer.Rail} {transfer.Amount:N2} {transfer.Currency} to {transfer.BeneficiaryName}");
        return (transfer, null);
    }

    public async Task<List<BankTransfer>> ListAsync(Guid accountId) =>
        await _context.BankTransfers.AsNoTracking()
            .Where(t => t.AccountId == accountId)
            .OrderByDescending(t => t.SubmittedAt)
            .ToListAsync();

    public async Task<BankTransfer?> GetAsync(Guid id) =>
        await _context.BankTransfers.AsNoTracking().FirstOrDefaultAsync(t => t.Id == id);

    public async Task<List<BankTransfer>> ListAllAsync(BankTransferStatus? status, int take)
    {
        var query = _context.BankTransfers.AsNoTracking().AsQueryable();
        if (status.HasValue) query = query.Where(t => t.Status == status.Value);
        return await query.OrderByDescending(t => t.SubmittedAt).Take(Math.Clamp(take, 1, 500)).ToListAsync();
    }

    public async Task<int> SettleDueAsync()
    {
        var now = DateTime.UtcNow;
        var changed = 0;

        var due = await _context.BankTransfers
            .Where(t => (t.Status == BankTransferStatus.Submitted || t.Status == BankTransferStatus.Processing)
                        && (t.Status == BankTransferStatus.Submitted || t.ExpectedSettlementAt <= now))
            .ToListAsync();

        foreach (var transfer in due)
        {
            if (transfer.Status == BankTransferStatus.Submitted)
            {
                // Picked up by the clearing house.
                transfer.Status = BankTransferStatus.Processing;
                changed++;
            }

            if (transfer.Status == BankTransferStatus.Processing && transfer.ExpectedSettlementAt <= now)
            {
                await _ledger.RecordSettlementAsync(transfer.Id, transfer.Amount, transfer.Currency, $"{transfer.Rail} settlement {transfer.Reference}");
                transfer.Status = BankTransferStatus.Settled;
                transfer.SettledAt = now;
                changed++;
            }
        }

        if (changed > 0) await _context.SaveChangesAsync();
        return changed;
    }

    public async Task<(bool Ok, string? Error)> ReturnAsync(Guid id, string reason, Guid staffUserId)
    {
        var transfer = await _context.BankTransfers.FirstOrDefaultAsync(t => t.Id == id);
        if (transfer == null) return (false, "Transfer not found");
        if (transfer.Status is BankTransferStatus.Returned or BankTransferStatus.Failed)
        {
            return (false, $"Transfer is already {transfer.Status}");
        }

        if (transfer.DebitTransactionId == null) return (false, "The transfer has no debit to reverse");

        var wasSettled = transfer.Status == BankTransferStatus.Settled;

        var credit = await _transactions.ReverseTransactionAsync(transfer.DebitTransactionId.Value, $"{transfer.Rail} returned: {reason}");
        if (credit == null) return (false, "The debit could not be reversed");

        if (wasSettled)
        {
            // The cash came back from the beneficiary bank: undo the settlement entry.
            await _ledger.RecordSettlementAsync(transfer.Id, transfer.Amount, transfer.Currency, $"{transfer.Rail} return {transfer.Reference}", reverse: true);
        }

        transfer.Status = BankTransferStatus.Returned;
        transfer.FailureReason = reason;
        await _context.SaveChangesAsync();
        await _audit.LogAsync("BankTransfer.Returned", staffUserId, "BankTransfer", id.ToString(), reason);
        return (true, null);
    }

    // ------------------------------------------------------------------ validation

    public static string? ValidateDetails(BankTransferRequest request, string accountCurrency)
    {
        switch (request.Rail)
        {
            case BankTransferRail.Ach:
            case BankTransferRail.Wire:
                if (!accountCurrency.Equals("USD", StringComparison.OrdinalIgnoreCase))
                    return $"{request.Rail} transfers are available for USD accounts only.";
                if (string.IsNullOrWhiteSpace(request.RoutingNumber) || !IsValidAbaRouting(request.RoutingNumber))
                    return "Enter a valid 9-digit ABA routing number.";
                if (string.IsNullOrWhiteSpace(request.BeneficiaryAccountNumber) || !Regex.IsMatch(request.BeneficiaryAccountNumber, @"^\d{4,17}$"))
                    return "Enter the beneficiary account number (4-17 digits).";
                if (request.Rail == BankTransferRail.Ach && request.Amount > 25_000m)
                    return "ACH transfers are limited to 25,000. Use a wire for larger amounts.";
                return null;

            default: // SWIFT
                if (string.IsNullOrWhiteSpace(request.SwiftBic) || !BicPattern.IsMatch(request.SwiftBic.Trim().ToUpperInvariant()))
                    return "Enter a valid SWIFT/BIC code (8 or 11 characters).";
                if (string.IsNullOrWhiteSpace(request.DestinationCountry) || !Regex.IsMatch(request.DestinationCountry, "^[A-Za-z]{2}$"))
                    return "Enter the 2-letter destination country code.";
                if (!string.IsNullOrWhiteSpace(request.Iban))
                {
                    if (!IsValidIban(request.Iban)) return "The IBAN is not valid.";
                }
                else if (string.IsNullOrWhiteSpace(request.BeneficiaryAccountNumber))
                {
                    return "Enter an IBAN or the beneficiary account number.";
                }
                return null;
        }
    }

    /// <summary>ABA routing number checksum: 3(d1+d4+d7) + 7(d2+d5+d8) + (d3+d6+d9) must be divisible by 10.</summary>
    public static bool IsValidAbaRouting(string routing)
    {
        if (!Regex.IsMatch(routing, @"^\d{9}$")) return false;

        var d = routing.Select(c => c - '0').ToArray();
        var checksum = 3 * (d[0] + d[3] + d[6]) + 7 * (d[1] + d[4] + d[7]) + (d[2] + d[5] + d[8]);
        return checksum % 10 == 0;
    }

    /// <summary>IBAN check: move the first four characters to the end, convert letters to numbers, mod 97 must equal 1.</summary>
    public static bool IsValidIban(string iban)
    {
        var normalized = Regex.Replace(iban, @"\s", "").ToUpperInvariant();
        if (!Regex.IsMatch(normalized, @"^[A-Z]{2}\d{2}[A-Z0-9]{11,30}$")) return false;

        var rearranged = normalized[4..] + normalized[..4];
        var remainder = 0;
        foreach (var ch in rearranged)
        {
            var value = char.IsDigit(ch) ? ch - '0' : ch - 'A' + 10;
            remainder = value < 10 ? (remainder * 10 + value) % 97 : (remainder * 100 + value) % 97;
        }

        return remainder == 1;
    }

    public static decimal CalculateFee(BankTransferRail rail, decimal amount) => rail switch
    {
        BankTransferRail.Ach => AchFee,
        BankTransferRail.Wire => WireFee,
        _ => Math.Round(SwiftBaseFee + amount * 0.001m, 2)
    };

    private static DateTime ExpectedSettlement(BankTransferRail rail, DateTime from) => rail switch
    {
        BankTransferRail.Wire => from.AddHours(2),
        BankTransferRail.Ach => AddBusinessDays(from, 2),
        _ => AddBusinessDays(from, 3)
    };

    private static DateTime AddBusinessDays(DateTime from, int days)
    {
        var result = from;
        while (days > 0)
        {
            result = result.AddDays(1);
            if (result.DayOfWeek is not (DayOfWeek.Saturday or DayOfWeek.Sunday)) days--;
        }
        return result;
    }
}
