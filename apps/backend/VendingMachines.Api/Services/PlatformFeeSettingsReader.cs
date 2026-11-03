using Microsoft.EntityFrameworkCore;
using VendingMachines.Api.Data;

namespace VendingMachines.Api.Services;

public static class PlatformFeeSettingsReader
{
    public static async Task<decimal?> ReadValidFeePercentAsync(
        AppDbContext db,
        CancellationToken cancellationToken = default)
    {
        var feeSettings = await db.SystemSettings.AsNoTracking()
            .Select(settings => settings.ApplicationFeePercent)
            .Take(2)
            .ToListAsync(cancellationToken);

        if (feeSettings.Count != 1 || feeSettings[0] <= 0m || feeSettings[0] >= 100m)
            return null;

        return feeSettings[0];
    }
}
