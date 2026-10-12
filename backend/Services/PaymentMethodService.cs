using BankCore.Services.Patterns;
using BankingAPI.Data;
using BankingAPI.DTOs;
using BankingAPI.Models;
using BankingAPI.Services.Security;
using Microsoft.EntityFrameworkCore;

namespace BankingAPI.Services;

public interface IPaymentMethodService
{
    Task<(PaymentMethod? Method, string? Error)> AddAsync(Guid userId, AddPaymentMethodRequest request);
    Task<List<PaymentMethod>> ListAsync(Guid userId);
    Task<bool> SetDefaultAsync(Guid userId, Guid id);
    Task<bool> RemoveAsync(Guid userId, Guid id);
}

/// <summary>
/// Stores tokenized payment methods. The gateway strategy turns card details (simulated mode) or a client-side
/// token (live mode) into a token; only the token, brand, last four digits and expiry are saved.
/// </summary>
public class PaymentMethodService : IPaymentMethodService
{
    private readonly ApplicationDbContext _context;
    private readonly PaymentStrategyFactory _strategies;
    private readonly IAuditService _audit;

    public PaymentMethodService(ApplicationDbContext context, PaymentStrategyFactory strategies, IAuditService audit)
    {
        _context = context;
        _strategies = strategies;
        _audit = audit;
    }

    public async Task<(PaymentMethod? Method, string? Error)> AddAsync(Guid userId, AddPaymentMethodRequest request)
    {
        IPaymentStrategy strategy;
        try
        {
            strategy = _strategies.GetStrategy(request.Provider);
        }
        catch (ArgumentException)
        {
            return (null, "Provider must be Stripe or PayPal.");
        }

        var result = await strategy.TokenizeAsync(new TokenizeRequest
        {
            ProviderToken = request.ProviderToken,
            CardNumber = request.CardNumber,
            ExpMonth = request.ExpMonth,
            ExpYear = request.ExpYear,
            Cvc = request.Cvc
        });

        if (!result.Success) return (null, result.Message);

        var hasAny = await _context.PaymentMethods.AnyAsync(m => m.UserId == userId && m.RemovedAt == null);
        var method = new PaymentMethod
        {
            Id = Guid.NewGuid(),
            UserId = userId,
            Provider = strategy.ProviderName,
            Token = result.Token,
            Brand = result.Brand,
            Last4 = result.Last4,
            ExpMonth = result.ExpMonth,
            ExpYear = result.ExpYear,
            IsDefault = request.MakeDefault || !hasAny
        };

        if (method.IsDefault)
        {
            await ClearDefaultAsync(userId);
        }

        _context.PaymentMethods.Add(method);
        await _context.SaveChangesAsync();
        await _audit.LogAsync("PaymentMethod.Added", userId, "PaymentMethod", method.Id.ToString(), $"{method.Provider} {method.Brand} ****{method.Last4}");
        return (method, null);
    }

    public async Task<List<PaymentMethod>> ListAsync(Guid userId) =>
        await _context.PaymentMethods.AsNoTracking()
            .Where(m => m.UserId == userId && m.RemovedAt == null)
            .OrderByDescending(m => m.IsDefault).ThenByDescending(m => m.CreatedAt)
            .ToListAsync();

    public async Task<bool> SetDefaultAsync(Guid userId, Guid id)
    {
        var method = await _context.PaymentMethods.FirstOrDefaultAsync(m => m.Id == id && m.UserId == userId && m.RemovedAt == null);
        if (method == null) return false;

        await ClearDefaultAsync(userId);
        method.IsDefault = true;
        await _context.SaveChangesAsync();
        return true;
    }

    public async Task<bool> RemoveAsync(Guid userId, Guid id)
    {
        var method = await _context.PaymentMethods.FirstOrDefaultAsync(m => m.Id == id && m.UserId == userId && m.RemovedAt == null);
        if (method == null) return false;

        method.RemovedAt = DateTime.UtcNow;
        method.IsDefault = false;
        await _context.SaveChangesAsync();
        await _audit.LogAsync("PaymentMethod.Removed", userId, "PaymentMethod", id.ToString());
        return true;
    }

    private async Task ClearDefaultAsync(Guid userId)
    {
        var defaults = await _context.PaymentMethods.Where(m => m.UserId == userId && m.IsDefault).ToListAsync();
        foreach (var existing in defaults) existing.IsDefault = false;
    }
}
