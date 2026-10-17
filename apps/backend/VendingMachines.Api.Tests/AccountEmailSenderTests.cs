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

    private static SmtpAccountEmailSender CreateSender(AccountEmailOptions options, string environmentName) =>
        new(
            Options.Create(options),
            new TestEnvironment(environmentName),
            Microsoft.Extensions.Logging.Abstractions.NullLogger<SmtpAccountEmailSender>.Instance);

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
