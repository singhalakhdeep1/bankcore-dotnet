using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using BankingAPI.DTOs;
using BankingAPI.Extensions;
using BankingAPI.Models;
using BankingAPI.Services;

namespace BankingAPI.Controllers;

/// <summary>The signed-in customer's profile, password, identity (KYC) documents and closure of the profile.</summary>
[ApiController]
[Route("api/[controller]")]
[Authorize]
public class UserController : ControllerBase
{
    private readonly IAuthService _authService;
    private readonly IKycService _kyc;

    public UserController(IAuthService authService, IKycService kyc)
    {
        _authService = authService;
        _kyc = kyc;
    }

    [HttpGet("profile")]
    public async Task<IActionResult> GetProfile()
    {
        if (User.GetUserId() is not { } userId) return Unauthorized();

        var user = await _authService.GetUserByIdAsync(userId);
        return user == null ? NotFound(new { message = "User not found" }) : Ok(user);
    }

    [HttpPut("profile")]
    public async Task<IActionResult> UpdateProfile([FromBody] UpdateProfileRequest request)
    {
        if (User.GetUserId() is not { } userId) return Unauthorized();

        var user = await _authService.UpdateProfileAsync(userId, request);
        return user == null ? NotFound(new { message = "User not found" }) : Ok(user);
    }

    [HttpPost("change-password")]
    public async Task<IActionResult> ChangePassword([FromBody] ChangePasswordRequest request)
    {
        if (User.GetUserId() is not { } userId) return Unauthorized();

        var problems = await _authService.ChangePasswordAsync(userId, request);
        return problems.Count == 0
            ? Ok(new { message = "Password changed." })
            : BadRequest(new { message = problems[0], problems });
    }

    /// <summary>Closes the profile (soft delete). All accounts must have a zero balance.</summary>
    [HttpDelete]
    public async Task<IActionResult> CloseProfile([FromBody] LoginPasswordConfirmation confirmation)
    {
        if (User.GetUserId() is not { } userId) return Unauthorized();

        var error = await _authService.DeleteAccountAsync(userId, confirmation.Password);
        return error == null ? Ok(new { message = "Your profile has been closed." }) : BadRequest(new { message = error });
    }

    /// <summary>Uploads an identity document (PDF, JPEG or PNG up to 5 MB) for KYC review.</summary>
    [HttpPost("kyc/documents")]
    [RequestSizeLimit(6 * 1024 * 1024)]
    public async Task<IActionResult> UploadKycDocument([FromForm] KycDocumentType documentType, IFormFile file)
    {
        if (User.GetUserId() is not { } userId) return Unauthorized();

        var (document, error) = await _kyc.UploadAsync(userId, documentType, file);
        return document == null ? BadRequest(new { message = error }) : StatusCode(StatusCodes.Status201Created, KycDocumentDto.From(document));
    }

    [HttpGet("kyc/documents")]
    public async Task<IActionResult> ListKycDocuments()
    {
        if (User.GetUserId() is not { } userId) return Unauthorized();

        var documents = await _kyc.ListAsync(userId);
        var profile = await _authService.GetUserByIdAsync(userId);
        return Ok(new { kycStatus = profile?.KycStatus, documents = documents.Select(KycDocumentDto.From) });
    }
}

public class LoginPasswordConfirmation
{
    public string Password { get; set; } = string.Empty;
}

public class KycDocumentDto
{
    public Guid Id { get; set; }
    public string DocumentType { get; set; } = string.Empty;
    public string FileName { get; set; } = string.Empty;
    public string ContentType { get; set; } = string.Empty;
    public long SizeBytes { get; set; }
    public string Status { get; set; } = string.Empty;
    public string? ReviewNotes { get; set; }
    public DateTime UploadedAt { get; set; }
    public DateTime? ReviewedAt { get; set; }

    public static KycDocumentDto From(KycDocument d) => new()
    {
        Id = d.Id,
        DocumentType = d.DocumentType.ToString(),
        FileName = d.OriginalFileName,
        ContentType = d.ContentType,
        SizeBytes = d.SizeBytes,
        Status = d.Status.ToString(),
        ReviewNotes = d.ReviewNotes,
        UploadedAt = d.UploadedAt,
        ReviewedAt = d.ReviewedAt
    };
}
