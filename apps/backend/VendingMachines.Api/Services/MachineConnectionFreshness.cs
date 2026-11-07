namespace VendingMachines.Api.Services;

public static class MachineConnectionFreshness
{
    public static readonly TimeSpan Window = TimeSpan.FromSeconds(90);
}
