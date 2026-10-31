namespace SiteLedger.Api.Auth;

public sealed class TokenOptions
{
    public const string SectionName = "Authentication:Jwt";
    public string Issuer { get; set; } = "";
    public string Audience { get; set; } = "";
    public string SigningKey { get; set; } = "";
    public int AccessTokenMinutes { get; set; } = 15;
}
