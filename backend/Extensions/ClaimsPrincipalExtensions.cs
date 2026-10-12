using System.Security.Claims;

namespace BankingAPI.Extensions;

public static class ClaimsPrincipalExtensions
{
    /// <summary>Returns the authenticated user's id from the JWT "sub" claim, or null if absent/invalid.</summary>
    public static Guid? GetUserId(this ClaimsPrincipal principal)
    {
        var value = principal.FindFirstValue("sub") ?? principal.FindFirstValue(ClaimTypes.NameIdentifier);
        return Guid.TryParse(value, out var id) ? id : null;
    }
}
