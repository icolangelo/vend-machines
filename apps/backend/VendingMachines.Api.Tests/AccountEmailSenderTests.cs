using System.Net;
using System.Net.Sockets;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Options;
using Microsoft.AspNetCore.Hosting;
using VendingMachines.Api.Services;
using Xunit;

namespace VendingMachines.Api.Tests;

public sealed class AccountEmailSenderTests
{
    [Fact]
    public async Task DisabledEmailDeliveryFallsBackWithoutNetworkActivity()
    {
        var sender = CreateSender(new AccountEmailOptions(), "Production");

        var sent = await sender.SendInvitationAsync(
            "person@example.com",
            "Operator",
            "one-time-token",
            DateTime.UtcNow.AddDays(7),
            CancellationToken.None);

        Assert.False(sent);
    }

    [Fact]
    public async Task ProductionEmailRejectsNonHttpsPublicLinkWithoutSending()
    {
        var sender = CreateSender(new AccountEmailOptions
        {
            Enabled = true,
            Host = "smtp.invalid",
            Port = 587,
            FromAddress = "no-reply@example.com",
            PublicBaseUrl = "http://app.example.com"
        }, "Production");

        var sent = await sender.SendPasswordResetAsync(
            "person@example.com",
            "one-time-token",
            DateTime.UtcNow.AddHours(1),
            CancellationToken.None);

        Assert.False(sent);
    }

    [Fact]
    public async Task SmtpFailureFallsBackWithoutLoggingTokenRecipientOrCredentials()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();

        var logger = new RecordingLogger();
        var sender = CreateSender(new AccountEmailOptions
        {
            Enabled = true,
            Host = IPAddress.Loopback.ToString(),
            Port = port,
            Username = "smtp-user-secret",
            Password = "smtp-password-secret",
            FromAddress = "no-reply@example.com",
            PublicBaseUrl = "https://app.example.com",
            TimeoutSeconds = 2
        }, "Production", logger);
        const string recipient = "private-recipient@example.com";
        const string token = "one-time-private-token";

        var sent = await sender.SendPasswordResetAsync(
            recipient,
            token,
            DateTime.UtcNow.AddHours(1),
            CancellationToken.None);

        Assert.False(sent);
        var log = Assert.Single(logger.Messages);
        Assert.Contains("Falha no envio SMTP", log, StringComparison.Ordinal);
        Assert.DoesNotContain(recipient, log, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(token, log, StringComparison.Ordinal);
        Assert.DoesNotContain("smtp-user-secret", log, StringComparison.Ordinal);
        Assert.DoesNotContain("smtp-password-secret", log, StringComparison.Ordinal);
    }

    private static SmtpAccountEmailSender CreateSender(
        AccountEmailOptions options,
        string environmentName,
        ILogger<SmtpAccountEmailSender>? logger = null) =>
        new(
            Options.Create(options),
            new TestEnvironment(environmentName),
            logger ?? Microsoft.Extensions.Logging.Abstractions.NullLogger<SmtpAccountEmailSender>.Instance);

    private sealed class RecordingLogger : ILogger<SmtpAccountEmailSender>
    {
        public List<string> Messages { get; } = [];
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            Messages.Add(formatter(state, exception));
        }
    }

    private sealed class TestEnvironment(string environmentName) : IWebHostEnvironment
    {
        public string EnvironmentName { get; set; } = environmentName;
        public string ApplicationName { get; set; } = "VendingMachines.Api.Tests";
        public string WebRootPath { get; set; } = string.Empty;
        public IFileProvider WebRootFileProvider { get; set; } = new NullFileProvider();
        public string ContentRootPath { get; set; } = string.Empty;
        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
    }
}
