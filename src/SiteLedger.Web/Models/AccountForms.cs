using System.ComponentModel.DataAnnotations;

namespace SiteLedger.Web.Models;

public abstract class AccountForm;

public sealed class LoginForm : AccountForm
{
    [Required, EmailAddress, StringLength(256)] public string Email { get; set; } = "";
    [Required, StringLength(256), DataType(DataType.Password)] public string Password { get; set; } = "";
    [Display(Name = "Authenticator code"), RegularExpression(@"^\d{6}$", ErrorMessage = "Enter the six digits from your authenticator.")]
    public string? AuthenticatorCode { get; set; }
    [Display(Name = "Recovery code"), StringLength(64)] public string? RecoveryCode { get; set; }
}

public sealed class RegistrationForm : AccountForm
{
    [Required, Display(Name = "Full name"), StringLength(200)] public string FullName { get; set; } = "";
    [Required, Display(Name = "Company name"), StringLength(200)] public string CompanyName { get; set; } = "";
    [Required, EmailAddress, StringLength(256)] public string Email { get; set; } = "";
    [Required, StringLength(256, MinimumLength = 6), DataType(DataType.Password)] public string Password { get; set; } = "";
    [Required, Display(Name = "Confirm password"), DataType(DataType.Password), Compare(nameof(Password), ErrorMessage = "The passwords do not match.")]
    public string ConfirmPassword { get; set; } = "";
}

public sealed class RecoveryRequestForm : AccountForm
{
    [Required, EmailAddress, StringLength(256)] public string Email { get; set; } = "";
    [Required, RegularExpression("^(reset|confirm)$", ErrorMessage = "Choose a password reset or confirmation request.")]
    public string ActionType { get; set; } = "";
}

public sealed class ResetPasswordForm : AccountForm
{
    [Required, EmailAddress, StringLength(256)] public string Email { get; set; } = "";
    [Required, Display(Name = "Reset token"), StringLength(4096), DataType(DataType.Password)] public string Token { get; set; } = "";
    [Required, Display(Name = "New password"), StringLength(256, MinimumLength = 6), DataType(DataType.Password)] public string Password { get; set; } = "";
    [Required, Display(Name = "Confirm password"), DataType(DataType.Password), Compare(nameof(Password), ErrorMessage = "The passwords do not match.")]
    public string ConfirmPassword { get; set; } = "";
}

public sealed class RecoveryPage
{
    public RecoveryRequestForm Request { get; set; } = new();
    public ResetPasswordForm Reset { get; set; } = new();
}
