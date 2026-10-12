using FluentValidation;

namespace BankingAPI.DTOs;

public class RegisterRequestValidator : AbstractValidator<RegisterRequest>
{
    public RegisterRequestValidator()
    {
        RuleFor(x => x.Email).NotEmpty().EmailAddress().MaximumLength(255);
        RuleFor(x => x.FirstName).NotEmpty().MaximumLength(100);
        RuleFor(x => x.LastName).NotEmpty().MaximumLength(100);
        RuleFor(x => x.Password).NotEmpty().MaximumLength(128);
        RuleFor(x => x.Phone).Matches(@"^[+\d][\d\s\-()]{5,28}$").When(x => !string.IsNullOrWhiteSpace(x.Phone))
            .WithMessage("Enter a valid phone number.");
    }
}

public class TransferRequestValidator : AbstractValidator<TransferRequest>
{
    public TransferRequestValidator()
    {
        RuleFor(x => x.FromAccountId).NotEmpty();
        RuleFor(x => x).Must(x => x.ToAccountId.HasValue || !string.IsNullOrWhiteSpace(x.ToAccountNumber))
            .WithName("Destination").WithMessage("Provide toAccountNumber or toAccountId.");
        RuleFor(x => x.Amount).GreaterThan(0).PrecisionScale(15, 2, false).WithMessage("Amount must be positive with at most 2 decimals.");
        RuleFor(x => x.IdempotencyKey).MaximumLength(64);
    }
}

public class CreateRecurringPaymentValidator : AbstractValidator<CreateRecurringPaymentRequest>
{
    public CreateRecurringPaymentValidator()
    {
        RuleFor(x => x.AccountId).NotEmpty();
        RuleFor(x => x.MerchantName).NotEmpty().MaximumLength(200);
        RuleFor(x => x.Amount).GreaterThan(0).PrecisionScale(15, 2, false).WithMessage("Amount must be positive with at most 2 decimals.");
    }
}

public class BankTransferRequestValidator : AbstractValidator<BankTransferRequest>
{
    public BankTransferRequestValidator()
    {
        RuleFor(x => x.AccountId).NotEmpty();
        RuleFor(x => x.BeneficiaryName).NotEmpty().MaximumLength(200);
        RuleFor(x => x.Amount).GreaterThan(0).PrecisionScale(15, 2, false).WithMessage("Amount must be positive with at most 2 decimals.");
    }
}
