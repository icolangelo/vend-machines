namespace VendingMachines.Api.Services;

public static class AppRoles
{
    public const string Admin = "Admin";
    public const string PlatformAdmin = "PlatformAdmin";
    public const string Operator = "Operator";
    public const string CompanyAdminOrPlatformAdmin = Admin + "," + PlatformAdmin;
}
