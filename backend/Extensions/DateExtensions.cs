namespace BankingAPI.Extensions;

public static class DateExtensions
{
    /// <summary>
    /// Normalizes a client-supplied date to UTC. Dates posted as "2026-01-31" arrive with Kind=Unspecified,
    /// which PostgreSQL (timestamptz) refuses; they are taken to mean UTC.
    /// </summary>
    public static DateTime AsUtc(this DateTime value) => value.Kind switch
    {
        DateTimeKind.Utc => value,
        DateTimeKind.Local => value.ToUniversalTime(),
        _ => DateTime.SpecifyKind(value, DateTimeKind.Utc)
    };

    public static DateTime? AsUtc(this DateTime? value) => value?.AsUtc();
}
