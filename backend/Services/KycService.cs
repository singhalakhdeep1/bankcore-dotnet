using System.Security.Cryptography;
using BankingAPI.Data;
using BankingAPI.Models;
using BankingAPI.Services.Security;
using Microsoft.EntityFrameworkCore;

namespace BankingAPI.Services;

public interface IKycService
{
    Task<(KycDocument? Document, string? Error)> UploadAsync(Guid userId, KycDocumentType type, IFormFile file);
    Task<List<KycDocument>> ListAsync(Guid userId);
    Task<List<KycDocument>> ListPendingAsync();
    Task<(Stream? Stream, KycDocument? Document)> OpenAsync(Guid documentId);
    Task<(bool Ok, string? Error)> ReviewDocumentAsync(Guid documentId, bool approve, string? notes, Guid reviewerUserId);
    Task<(bool Ok, string? Error)> SetUserStatusAsync(Guid userId, KycStatus status, string? notes, Guid reviewerUserId);
}

/// <summary>
/// KYC: customers upload identity documents, staff approve or reject them, and the customer's
/// KYC status follows. Files are checked by content (not just extension), hashed, and stored under
/// random names outside the web root. Document metadata is mirrored to RavenDB when it is configured.
/// </summary>
public class KycService : IKycService
{
    private const long MaxBytes = 5 * 1024 * 1024;

    private readonly ApplicationDbContext _context;
    private readonly IAuditService _audit;
    private readonly IServiceProvider _services;
    private readonly string _storagePath;

    public KycService(ApplicationDbContext context, IAuditService audit, IServiceProvider services, IConfiguration configuration, IHostEnvironment environment)
    {
        _context = context;
        _audit = audit;
        _services = services;

        var configured = configuration["Storage:KycPath"];
        _storagePath = string.IsNullOrWhiteSpace(configured)
            ? Path.Combine(environment.ContentRootPath, "App_Data", "kyc")
            : configured;
    }

    public async Task<(KycDocument? Document, string? Error)> UploadAsync(Guid userId, KycDocumentType type, IFormFile file)
    {
        if (file.Length == 0) return (null, "The file is empty.");
        if (file.Length > MaxBytes) return (null, "The file is larger than 5 MB.");

        byte[] bytes;
        await using (var stream = file.OpenReadStream())
        {
            using var memory = new MemoryStream();
            await stream.CopyToAsync(memory);
            bytes = memory.ToArray();
        }

        var contentType = DetectContentType(bytes);
        if (contentType == null) return (null, "Only PDF, JPEG and PNG files are accepted.");

        var pendingCount = await _context.KycDocuments.CountAsync(d => d.UserId == userId && d.Status == KycDocumentStatus.Pending);
        if (pendingCount >= 10) return (null, "Too many documents are awaiting review.");

        Directory.CreateDirectory(_storagePath);
        var storedName = $"{Guid.NewGuid():N}{ExtensionFor(contentType)}";
        await File.WriteAllBytesAsync(Path.Combine(_storagePath, storedName), bytes);

        var document = new KycDocument
        {
            Id = Guid.NewGuid(),
            UserId = userId,
            DocumentType = type,
            OriginalFileName = Path.GetFileName(file.FileName),
            StoredFileName = storedName,
            ContentType = contentType,
            SizeBytes = bytes.Length,
            Sha256 = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant()
        };

        _context.KycDocuments.Add(document);
        await _context.SaveChangesAsync();
        await _audit.LogAsync("Kyc.DocumentUploaded", userId, "KycDocument", document.Id.ToString(), $"{type} ({contentType}, {bytes.Length} bytes)");
        await MirrorToRavenAsync(document);
        return (document, null);
    }

    public async Task<List<KycDocument>> ListAsync(Guid userId) =>
        await _context.KycDocuments.AsNoTracking()
            .Where(d => d.UserId == userId)
            .OrderByDescending(d => d.UploadedAt)
            .ToListAsync();

    public async Task<List<KycDocument>> ListPendingAsync() =>
        await _context.KycDocuments.AsNoTracking()
            .Include(d => d.User)
            .Where(d => d.Status == KycDocumentStatus.Pending)
            .OrderBy(d => d.UploadedAt)
            .ToListAsync();

    public async Task<(Stream? Stream, KycDocument? Document)> OpenAsync(Guid documentId)
    {
        var document = await _context.KycDocuments.AsNoTracking().FirstOrDefaultAsync(d => d.Id == documentId);
        if (document == null) return (null, null);

        var path = Path.Combine(_storagePath, document.StoredFileName);
        return File.Exists(path) ? (File.OpenRead(path), document) : (null, document);
    }

    public async Task<(bool Ok, string? Error)> ReviewDocumentAsync(Guid documentId, bool approve, string? notes, Guid reviewerUserId)
    {
        var document = await _context.KycDocuments.Include(d => d.User).FirstOrDefaultAsync(d => d.Id == documentId);
        if (document == null) return (false, "Document not found");
        if (document.Status != KycDocumentStatus.Pending) return (false, $"Document is already {document.Status}");

        document.Status = approve ? KycDocumentStatus.Approved : KycDocumentStatus.Rejected;
        document.ReviewNotes = notes;
        document.ReviewedByUserId = reviewerUserId;
        document.ReviewedAt = DateTime.UtcNow;

        // Verified once there is an approved identity document and an approved proof of address.
        if (approve && document.User.KycStatus != KycStatus.Verified)
        {
            var approved = await _context.KycDocuments.AsNoTracking()
                .Where(d => d.UserId == document.UserId && d.Status == KycDocumentStatus.Approved)
                .Select(d => d.DocumentType)
                .ToListAsync();
            approved.Add(document.DocumentType);

            var hasIdentity = approved.Any(t => t is KycDocumentType.Passport or KycDocumentType.NationalId or KycDocumentType.DrivingLicense);
            var hasAddress = approved.Contains(KycDocumentType.ProofOfAddress);
            if (hasIdentity && hasAddress) document.User.KycStatus = KycStatus.Verified;
        }

        await _context.SaveChangesAsync();
        await _audit.LogAsync(approve ? "Kyc.DocumentApproved" : "Kyc.DocumentRejected", reviewerUserId, "KycDocument", documentId.ToString(), notes);
        await MirrorToRavenAsync(document);
        return (true, null);
    }

    public async Task<(bool Ok, string? Error)> SetUserStatusAsync(Guid userId, KycStatus status, string? notes, Guid reviewerUserId)
    {
        var user = await _context.Users.FirstOrDefaultAsync(u => u.Id == userId);
        if (user == null) return (false, "User not found");

        user.KycStatus = status;
        user.UpdatedAt = DateTime.UtcNow;
        await _context.SaveChangesAsync();
        await _audit.LogAsync("Kyc.StatusSet", reviewerUserId, "User", userId.ToString(), $"{status}: {notes}");
        return (true, null);
    }

    /// <summary>Magic-byte sniffing: the declared content type and extension are not trusted.</summary>
    public static string? DetectContentType(byte[] bytes)
    {
        if (bytes.Length >= 5 && bytes[0] == 0x25 && bytes[1] == 0x50 && bytes[2] == 0x44 && bytes[3] == 0x46 && bytes[4] == 0x2D) return "application/pdf";
        if (bytes.Length >= 3 && bytes[0] == 0xFF && bytes[1] == 0xD8 && bytes[2] == 0xFF) return "image/jpeg";
        if (bytes.Length >= 8 && bytes[0] == 0x89 && bytes[1] == 0x50 && bytes[2] == 0x4E && bytes[3] == 0x47
            && bytes[4] == 0x0D && bytes[5] == 0x0A && bytes[6] == 0x1A && bytes[7] == 0x0A) return "image/png";
        return null;
    }

    private static string ExtensionFor(string contentType) => contentType switch
    {
        "application/pdf" => ".pdf",
        "image/jpeg" => ".jpg",
        _ => ".png"
    };

    private async Task MirrorToRavenAsync(KycDocument document)
    {
        var raven = _services.GetService<RavenDBService>();
        if (raven == null) return;

        try
        {
            await raven.StoreAsync(new
            {
                Id = $"kycdocuments/{document.Id}",
                document.UserId,
                DocumentType = document.DocumentType.ToString(),
                document.OriginalFileName,
                document.ContentType,
                document.SizeBytes,
                document.Sha256,
                Status = document.Status.ToString(),
                document.UploadedAt,
                document.ReviewedAt
            });
        }
        catch (Exception)
        {
            // The mirror is best-effort; PostgreSQL remains the system of record.
        }
    }
}
