using System.Reflection;
using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.Extensions.DependencyInjection;
using VendingMachines.Api.Controllers;
using VendingMachines.Api.Services;
using Xunit;

namespace VendingMachines.Api.Tests;

public sealed class RoleMatrixTests
{
    [Theory]
    [InlineData(typeof(MachinesController), nameof(MachinesController.Create))]
    [InlineData(typeof(MachinesController), nameof(MachinesController.Update))]
    [InlineData(typeof(MachinesController), nameof(MachinesController.RotateTelemetryCredential))]
    [InlineData(typeof(MachinesController), nameof(MachinesController.RevokeTelemetryCredential))]
    [InlineData(typeof(LocationsController), nameof(LocationsController.Create))]
    [InlineData(typeof(LocationsController), nameof(LocationsController.Update))]
    [InlineData(typeof(LocationsController), nameof(LocationsController.Delete))]
    [InlineData(typeof(ProductsController), nameof(ProductsController.CreateProduct))]
    [InlineData(typeof(ProductsController), nameof(ProductsController.UpdateProduct))]
    [InlineData(typeof(ProductsController), nameof(ProductsController.CreateProductType))]
    [InlineData(typeof(ProductsController), nameof(ProductsController.UpdateProductType))]
    [InlineData(typeof(ProductsController), nameof(ProductsController.DeleteProduct))]
    [InlineData(typeof(InventoryController), nameof(InventoryController.ConfigureSlot))]
    [InlineData(typeof(InventoryController), nameof(InventoryController.Restock))]
    [InlineData(typeof(InventoryController), nameof(InventoryController.AdjustStock))]
    [InlineData(typeof(TelemetryController), nameof(TelemetryController.SetMonitoring))]
    [InlineData(typeof(TelemetryController), nameof(TelemetryController.StartSession))]
    [InlineData(typeof(TelemetryController), nameof(TelemetryController.RequestMdbStatus))]
    [InlineData(typeof(TelemetryController), nameof(TelemetryController.CancelSession))]
    [InlineData(typeof(TelemetryController), nameof(TelemetryController.SendCommand))]
    [InlineData(typeof(PaymentsController), nameof(PaymentsController.OAuthCallback))]
    [InlineData(typeof(PaymentsController), nameof(PaymentsController.DisconnectIntegration))]
    [InlineData(typeof(PaymentsController), nameof(PaymentsController.ToggleIntegration))]
    [InlineData(typeof(PaymentsController), nameof(PaymentsController.CreatePixQrCode))]
    [InlineData(typeof(PaymentsController), nameof(PaymentsController.SimulateWebhook))]
    public void SensitiveWriteRequiresCompanyOrPlatformAdmin(Type controllerType, string methodName)
    {
        var method = controllerType.GetMethod(methodName, BindingFlags.Instance | BindingFlags.Public);

        Assert.NotNull(method);
        var authorize = method!.GetCustomAttribute<AuthorizeAttribute>();

        Assert.NotNull(authorize);
        Assert.Equal(AppRoles.CompanyAdminOrPlatformAdmin, authorize!.Roles);
    }

    [Theory]
    [InlineData(typeof(UsersController), nameof(UsersController.CreatePasswordResetLink))]
    [InlineData(typeof(UsersController), nameof(UsersController.UpdateRole))]
    [InlineData(typeof(UsersController), nameof(UsersController.UpdateActiveStatus))]
    public void UserAdministrationRequiresCompanyOrPlatformAdmin(Type controllerType, string methodName)
    {
        var method = controllerType.GetMethod(methodName, BindingFlags.Instance | BindingFlags.Public);

        Assert.NotNull(method);
        var authorize = method!.GetCustomAttribute<AuthorizeAttribute>();

        Assert.NotNull(authorize);
        Assert.Equal(AppRoles.CompanyAdminOrPlatformAdmin, authorize!.Roles);
    }

    [Theory]
    [InlineData(nameof(UsersController.CreateInvitation))]
    [InlineData(nameof(UsersController.RevokeInvitation))]
    public void InvitationManagementRequiresCompanyAdmin(string methodName)
    {
        var method = typeof(UsersController).GetMethod(methodName, BindingFlags.Instance | BindingFlags.Public);

        Assert.NotNull(method);
        var authorize = method!.GetCustomAttribute<AuthorizeAttribute>();

        Assert.NotNull(authorize);
        Assert.Equal(AppRoles.Admin, authorize!.Roles);
    }

    [Fact]
    public void AuditLogQueryRequiresPlatformAdmin()
    {
        var authorize = typeof(AuditLogsController).GetCustomAttribute<AuthorizeAttribute>();

        Assert.NotNull(authorize);
        Assert.Equal(AppRoles.PlatformAdmin, authorize!.Roles);
    }

    [Theory]
    [InlineData(nameof(PaymentsController.GetGlobalSettings))]
    [InlineData(nameof(PaymentsController.UpdateGlobalSettings))]
    [InlineData(nameof(PaymentsController.GetAdminLogs))]
    [InlineData(nameof(PaymentsController.GetWebhookInbox))]
    [InlineData(nameof(PaymentsController.GetReconciliationQueue))]
    [InlineData(nameof(PaymentsController.RetryWebhook))]
    [InlineData(nameof(CompaniesController.UpdateActivationStatus))]
    [InlineData(nameof(DeviceMessageInboxController.Retry))]
    public void PaymentPlatformOperationsRequirePlatformAdmin(string methodName)
    {
        var controllerType = methodName == nameof(CompaniesController.UpdateActivationStatus)
            ? typeof(CompaniesController)
            : methodName == nameof(DeviceMessageInboxController.Retry)
                ? typeof(DeviceMessageInboxController)
                : typeof(PaymentsController);
        var method = controllerType.GetMethod(methodName, BindingFlags.Instance | BindingFlags.Public);

        Assert.NotNull(method);
        var authorize = method!.GetCustomAttribute<AuthorizeAttribute>()
            ?? controllerType.GetCustomAttribute<AuthorizeAttribute>();

        Assert.NotNull(authorize);
        Assert.Equal(AppRoles.PlatformAdmin, authorize!.Roles);
    }

    [Theory]
    [InlineData(AppRoles.Admin, true)]
    [InlineData(AppRoles.PlatformAdmin, true)]
    [InlineData(AppRoles.Operator, false)]
    public async Task SensitiveWriteAuthorizationIsEnforcedByAuthorizationService(string role, bool expected)
    {
        var allowed = await IsAuthorizedAsync(
            typeof(MachinesController),
            nameof(MachinesController.Create),
            role);

        Assert.Equal(expected, allowed);
    }

    [Theory]
    [InlineData(AppRoles.Admin, false)]
    [InlineData(AppRoles.Operator, false)]
    [InlineData(AppRoles.PlatformAdmin, true)]
    public async Task AuditLogAuthorizationIsEnforcedByAuthorizationService(string role, bool expected)
    {
        var allowed = await IsAuthorizedAsync(typeof(AuditLogsController), nameof(AuditLogsController.Get), role);

        Assert.Equal(expected, allowed);
    }

    [Theory]
    [InlineData(AppRoles.Admin, true)]
    [InlineData(AppRoles.PlatformAdmin, true)]
    [InlineData(AppRoles.Operator, false)]
    public async Task PaymentIntegrationWriteAuthorizationIsEnforcedByAuthorizationService(string role, bool expected)
    {
        foreach (var methodName in new[]
                 {
                     nameof(PaymentsController.OAuthCallback),
                     nameof(PaymentsController.DisconnectIntegration),
                     nameof(PaymentsController.ToggleIntegration),
                     nameof(PaymentsController.SimulateWebhook)
                 })
        {
            var allowed = await IsAuthorizedAsync(typeof(PaymentsController), methodName, role);
            Assert.Equal(expected, allowed);
        }
    }

    [Theory]
    [InlineData(AppRoles.Admin, true)]
    [InlineData(AppRoles.PlatformAdmin, true)]
    [InlineData(AppRoles.Operator, false)]
    public async Task CreatingPixChargeRequiresAdministrator(string role, bool expected)
    {
        var allowed = await IsAuthorizedAsync(
            typeof(PaymentsController),
            nameof(PaymentsController.CreatePixQrCode),
            role);

        Assert.Equal(expected, allowed);
    }

    [Theory]
    [InlineData(AppRoles.Admin, true)]
    [InlineData(AppRoles.PlatformAdmin, true)]
    [InlineData(AppRoles.Operator, false)]
    public async Task InventoryAndUserAdministrationAuthorizationIsEnforced(string role, bool expected)
    {
        foreach (var (controllerType, methodName) in new[]
                 {
                     (typeof(InventoryController), nameof(InventoryController.ConfigureSlot)),
                     (typeof(InventoryController), nameof(InventoryController.Restock)),
                     (typeof(InventoryController), nameof(InventoryController.AdjustStock)),
                     (typeof(UsersController), nameof(UsersController.CreatePasswordResetLink)),
                     (typeof(UsersController), nameof(UsersController.UpdateRole)),
                     (typeof(UsersController), nameof(UsersController.UpdateActiveStatus))
                 })
        {
            var allowed = await IsAuthorizedAsync(controllerType, methodName, role);
            Assert.Equal(expected, allowed);
        }
    }

    [Theory]
    [InlineData(AppRoles.Admin, true)]
    [InlineData(AppRoles.PlatformAdmin, false)]
    [InlineData(AppRoles.Operator, false)]
    public async Task InvitationAuthorizationIsEnforced(string role, bool expected)
    {
        foreach (var methodName in new[]
                 {
                     nameof(UsersController.CreateInvitation),
                     nameof(UsersController.RevokeInvitation)
                 })
        {
            var allowed = await IsAuthorizedAsync(typeof(UsersController), methodName, role);
            Assert.Equal(expected, allowed);
        }
    }

    [Theory]
    [InlineData(AppRoles.PlatformAdmin, true)]
    [InlineData(AppRoles.Admin, false)]
    [InlineData(AppRoles.Operator, false)]
    public async Task PaymentPlatformAuthorizationIsEnforcedByAuthorizationService(string role, bool expected)
    {
        foreach (var methodName in new[]
                 {
                     nameof(PaymentsController.GetGlobalSettings),
                     nameof(PaymentsController.UpdateGlobalSettings),
                     nameof(PaymentsController.GetAdminLogs),
                     nameof(PaymentsController.GetWebhookInbox),
                     nameof(PaymentsController.GetReconciliationQueue),
                     nameof(PaymentsController.RetryWebhook)
                 })
        {
            var allowed = await IsAuthorizedAsync(typeof(PaymentsController), methodName, role);
            Assert.Equal(expected, allowed);
        }
    }

    [Theory]
    [InlineData(AppRoles.PlatformAdmin, true)]
    [InlineData(AppRoles.Admin, false)]
    [InlineData(AppRoles.Operator, false)]
    public async Task CompanyActivationAndDeviceInboxAuthorizationIsEnforced(string role, bool expected)
    {
        foreach (var (controllerType, methodName) in new[]
                 {
                     (typeof(CompaniesController), nameof(CompaniesController.UpdateActivationStatus)),
                     (typeof(DeviceMessageInboxController), nameof(DeviceMessageInboxController.Retry))
                 })
        {
            var allowed = await IsAuthorizedAsync(controllerType, methodName, role);
            Assert.Equal(expected, allowed);
        }
    }

    private static async Task<bool> IsAuthorizedAsync(Type controllerType, string methodName, string role)
    {
        var method = controllerType.GetMethod(methodName, BindingFlags.Instance | BindingFlags.Public);
        Assert.NotNull(method);
        var authorizeData = controllerType.GetCustomAttributes<AuthorizeAttribute>(inherit: true)
            .Concat(method!.GetCustomAttributes<AuthorizeAttribute>(inherit: true))
            .ToArray();

        var services = new ServiceCollection()
            .AddLogging()
            .AddAuthorizationCore()
            .BuildServiceProvider();
        var policyProvider = services.GetRequiredService<IAuthorizationPolicyProvider>();
        var policy = await AuthorizationPolicy.CombineAsync(policyProvider, authorizeData);
        Assert.NotNull(policy);

        var principal = new ClaimsPrincipal(new ClaimsIdentity(
            new[] { new Claim(ClaimTypes.Role, role) },
            authenticationType: "test"));
        var authorization = services.GetRequiredService<IAuthorizationService>();
        var result = await authorization.AuthorizeAsync(principal, resource: null, policy!);
        return result.Succeeded;
    }
}
