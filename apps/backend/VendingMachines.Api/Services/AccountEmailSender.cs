using System.Net;
using System.Net.Mail;
using System.Text;
using Microsoft.Extensions.Options;

namespace VendingMachines.Api.Services;

public sealed class AccountEmailOptions
{
    public const string SectionName = "AccountEmail";

    public bool Enabled { get; set; }
    public string? Host { get; set; }
    public int Port { get; set; } = 587;
    public string? Username { get; set; }
    public string? Password { get; set; }
    public string? FromAddress { get; set; }
    public string FromName { get; set; } = "Vend Machines";
    public string? PublicBaseUrl { get; set; }
    public int TimeoutSeconds { get; set; } = 15;
}

public interface IAccountEmailSender
{
    Task<bool> SendEmailVerificationAsync(
        string recipient,
        string token,
        DateTime expiresAt,
        CancellationToken cancellationToken);

    Task<bool> SendInvitationAsync(
        string recipient,
        string role,
        string token,
        DateTime expiresAt,
        CancellationToken cancellationToken);

    Task<bool> SendPasswordResetAsync(
        string recipient,
        string token,
        DateTime expiresAt,
        CancellationToken cancellationToken);
}

public sealed class SmtpAccountEmailSender : IAccountEmailSender
{
    private readonly AccountEmailOptions _options;
    private readonly IWebHostEnvironment _environment;
    private readonly ILogger<SmtpAccountEmailSender> _logger;

    public SmtpAccountEmailSender(
        IOptions<AccountEmailOptions> options,
        IWebHostEnvironment environment,
        ILogger<SmtpAccountEmailSender> logger)
    {
        _options = options.Value;
        _environment = environment;
        _logger = logger;
    }

    public Task<bool> SendInvitationAsync(
        string recipient,
        string role,
        string token,
        DateTime expiresAt,
        CancellationToken cancellationToken)
    {
        var roleLabel = role == "Admin" ? "administrador" : "operador";
        return SendLinkAsync(
            recipient,
            "Convite para acessar o Vend Machines",
            $"Você recebeu um convite para acessar o Vend Machines como {roleLabel}.",
            "invite",
            token,
            expiresAt,
            cancellationToken);
    }

    public Task<bool> SendEmailVerificationAsync(
        string recipient,
        string token,
        DateTime expiresAt,
        CancellationToken cancellationToken) =>
        SendLinkAsync(
            recipient,
            "Confirme seu e-mail no Vend Machines",
            "Confirme que este endereço de e-mail pertence a você para concluir o cadastro da empresa.",
            "verify-email",
            token,
            expiresAt,
            cancellationToken);

    public Task<bool> SendPasswordResetAsync(
        string recipient,
        string token,
        DateTime expiresAt,
        CancellationToken cancellationToken) =>
        SendLinkAsync(
            recipient,
            "Recuperação de acesso ao Vend Machines",
            "Um administrador solicitou um link para você recuperar o acesso ao Vend Machines.",
            "reset-password",
            token,
            expiresAt,
            cancellationToken);

    private async Task<bool> SendLinkAsync(
        string recipient,
        string subject,
        string introduction,
        string route,
        string token,
        DateTime expiresAt,
        CancellationToken cancellationToken)
    {
        if (!_options.Enabled)
            return false;

        if (!TryCreateLink(route, token, out var link) ||
            string.IsNullOrWhiteSpace(_options.Host) ||
            _options.Port is < 1 or > 65535 ||
            _options.TimeoutSeconds is < 1 or > 120 ||
            !MailAddress.TryCreate(_options.FromAddress, out var from) ||
            !MailAddress.TryCreate(recipient, out var to) ||
            string.IsNullOrWhiteSpace(_options.Username) != string.IsNullOrWhiteSpace(_options.Password))
        {
            _logger.LogWarning("Envio de e-mail de conta não configurado corretamente; o link será disponibilizado para compartilhamento manual.");
            return false;
        }

        try
        {
            using var message = new MailMessage(from!, to!)
            {
                Subject = subject,
                SubjectEncoding = Encoding.UTF8,
                BodyEncoding = Encoding.UTF8,
                Body = $"Olá,\n\n{introduction}\n\nAcesse o link abaixo:\n{link}\n\nEste link expira em {expiresAt.ToUniversalTime():dd/MM/yyyy HH:mm} UTC e só pode ser usado uma vez. Se você não esperava esta mensagem, ignore-a.\n"
            };
            if (!string.IsNullOrWhiteSpace(_options.FromName))
                message.From = new MailAddress(from!.Address, _options.FromName, Encoding.UTF8);

            using var client = new SmtpClient(_options.Host, _options.Port)
            {
                EnableSsl = true,
                UseDefaultCredentials = false,
                Timeout = _options.TimeoutSeconds * 1000
            };
            if (!string.IsNullOrWhiteSpace(_options.Username))
                client.Credentials = new NetworkCredential(_options.Username, _options.Password);

            using var sendTimeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            sendTimeout.CancelAfter(TimeSpan.FromSeconds(_options.TimeoutSeconds));
            await client.SendMailAsync(message, sendTimeout.Token);
            return true;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            _logger.LogWarning(
                "Falha no envio SMTP de e-mail de conta ({FailureType}); o link será disponibilizado para compartilhamento manual.",
                exception.GetType().Name);
            return false;
        }
    }

    private bool TryCreateLink(string route, string token, out Uri link)
    {
        link = null!;
        if (!Uri.TryCreate(_options.PublicBaseUrl, UriKind.Absolute, out var baseUri) ||
            !string.IsNullOrEmpty(baseUri.UserInfo) ||
            !string.IsNullOrEmpty(baseUri.Query) ||
            !string.IsNullOrEmpty(baseUri.Fragment))
            return false;

        var secure = baseUri.Scheme.Equals(Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase);
        var localDevelopment = _environment.IsDevelopment() &&
                               baseUri.Scheme.Equals(Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase) &&
                               (baseUri.IsLoopback || baseUri.Host.Equals("localhost", StringComparison.OrdinalIgnoreCase));
        if (!secure && !localDevelopment)
            return false;

        link = new Uri(baseUri, $"#/" + route + "?token=" + Uri.EscapeDataString(token));
        return true;
    }
}
