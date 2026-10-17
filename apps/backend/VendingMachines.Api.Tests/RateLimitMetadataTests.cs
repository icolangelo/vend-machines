using System.Reflection;
using Microsoft.AspNetCore.RateLimiting;
using VendingMachines.Api.Controllers;
using Xunit;

namespace VendingMachines.Api.Tests;

public sealed class RateLimitMetadataTests
{
    [Theory]
    [InlineData(typeof(AuthController), nameof(AuthController.Login), "auth")]
    [InlineData(typeof(AuthController), nameof(AuthController.Register), "auth")]
    [InlineData(typeof(AuthController), nameof(AuthController.AcceptInvitation), "auth")]
    [InlineData(typeof(AuthController), nameof(AuthController.ResetPassword), "auth")]
    [InlineData(typeof(AuthController), nameof(AuthController.Refresh), "auth")]
    [InlineData(typeof(UsersController), nameof(UsersController.CreateInvitation), "auth")]
    [InlineData(typeof(UsersController), nameof(UsersController.CreatePasswordResetLink), "auth")]
    [InlineData(typeof(TelemetryController), nameof(TelemetryController.CreateSocketTicket), "telemetry-ticket")]
    public void SensitiveEntryPointUsesExpectedRateLimit(Type controllerType, string methodName, string policyName)
    {
        var method = controllerType.GetMethod(methodName, BindingFlags.Instance | BindingFlags.Public);

        Assert.NotNull(method);
        var metadata = method!.GetCustomAttribute<EnableRateLimitingAttribute>();

        Assert.NotNull(metadata);
        Assert.Equal(policyName, metadata!.PolicyName);
    }
}
