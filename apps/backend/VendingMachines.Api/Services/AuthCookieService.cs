using Microsoft.Extensions.Options;

namespace VendingMachines.Api.Services;

public sealed class AuthCookieService
{
    private readonly AuthOptions _options;
    private readonly IWebHostEnvironment _environment;

    public AuthCookieService(IOptions<AuthOptions> options, IWebHostEnvironment environment)
    {
        _options = options.Value;
        _environment = environment;
    }

    public string CookieName => _options.RefreshCookieName
        ?? (_environment.IsDevelopment() ? "vm_refresh" : "__Host-vm_refresh");

    public string? Read(HttpRequest request)
    {
        return request.Cookies.TryGetValue(CookieName, out var token) ? token : null;
    }

    public void Write(HttpResponse response, string token, DateTime expiresAt)
    {
        response.Cookies.Append(CookieName, token, CreateOptions(expiresAt));
    }

    public void Delete(HttpResponse response)
    {
        response.Cookies.Delete(CookieName, CreateOptions(DateTime.UnixEpoch));
    }

    private CookieOptions CreateOptions(DateTime expiresAt)
    {
        return new CookieOptions
        {
            HttpOnly = true,
            Secure = !_environment.IsDevelopment(),
            SameSite = ParseSameSite(_options.RefreshCookieSameSite),
            Path = "/",
            Expires = new DateTimeOffset(DateTime.SpecifyKind(expiresAt, DateTimeKind.Utc)),
            IsEssential = true
        };
    }

    private static SameSiteMode ParseSameSite(string value)
    {
        return Enum.TryParse<SameSiteMode>(value, true, out var sameSite)
            ? sameSite
            : SameSiteMode.Strict;
    }
}
