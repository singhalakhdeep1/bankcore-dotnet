namespace BankingAPI.Models;

/// <summary>
/// Rule-based monitoring alert raised against a transaction (large amount, high frequency, unusual amount).
/// </summary>
public class ComplianceAlert
{
    public Guid Id { get; set; }
    public Guid AccountId { get; set; }
    public Guid TransactionId { get; set; }
    public string AlertType { get; set; } = string.Empty;
    public string Severity { get; set; } = "Low";
    public string Description { get; set; } = string.Empty;
    public DateTime CreatedAt { get; set; }

    public Account Account { get; set; } = null!;
    public Transaction Transaction { get; set; } = null!;
}
