namespace BankingAPI.Models;

/// <summary>Append-only record of a security- or money-relevant action.</summary>
public class AuditLog
{
    public Guid Id { get; set; }
    public DateTime Timestamp { get; set; } = DateTime.UtcNow;
    public Guid? UserId { get; set; }
    public string Action { get; set; } = string.Empty;
    public string? EntityType { get; set; }
    public string? EntityId { get; set; }
    public string? Details { get; set; }
    public string? IpAddress { get; set; }
    public string? CorrelationId { get; set; }
}

/// <summary>Long-lived credential for server-to-server access. Only a hash of the key is stored.</summary>
public class ApiKey
{
    public Guid Id { get; set; }
    public Guid OwnerUserId { get; set; }
    public string Name { get; set; } = string.Empty;

    /// <summary>First characters of the key, safe to show so keys can be told apart.</summary>
    public string Prefix { get; set; } = string.Empty;

    public string KeyHash { get; set; } = string.Empty;
    public string Scopes { get; set; } = "read";
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime? ExpiresAt { get; set; }
    public DateTime? RevokedAt { get; set; }
    public DateTime? LastUsedAt { get; set; }

    public bool IsActive => RevokedAt == null && (ExpiresAt == null || ExpiresAt > DateTime.UtcNow);
}

public enum KycDocumentType
{
    Passport,
    NationalId,
    DrivingLicense,
    ProofOfAddress
}

public enum KycDocumentStatus
{
    Pending,
    Approved,
    Rejected
}

/// <summary>An identity document uploaded for KYC. The file lives on disk; this row is its metadata.</summary>
public class KycDocument
{
    public Guid Id { get; set; }
    public Guid UserId { get; set; }
    public KycDocumentType DocumentType { get; set; }
    public string OriginalFileName { get; set; } = string.Empty;
    public string StoredFileName { get; set; } = string.Empty;
    public string ContentType { get; set; } = string.Empty;
    public long SizeBytes { get; set; }
    public string Sha256 { get; set; } = string.Empty;
    public KycDocumentStatus Status { get; set; } = KycDocumentStatus.Pending;
    public string? ReviewNotes { get; set; }
    public Guid? ReviewedByUserId { get; set; }
    public DateTime UploadedAt { get; set; } = DateTime.UtcNow;
    public DateTime? ReviewedAt { get; set; }

    public User User { get; set; } = null!;
}
