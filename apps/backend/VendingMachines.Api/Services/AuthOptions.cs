namespace VendingMachines.Api.Services;

public sealed class AuthOptions
{
    public const string SectionName = "Auth";

    public int AccessTokenMinutes { get; set; } = 15;
    public int RefreshTokenDays { get; set; } = 30;
    public int RefreshReuseGraceSeconds { get; set; } = 10;
    public string? RefreshCookieName { get; set; }
    public string RefreshCookieSameSite { get; set; } = "Strict";
}
