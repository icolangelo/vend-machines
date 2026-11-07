using Microsoft.EntityFrameworkCore;
using VendingMachines.Api.Data;
using VendingMachines.Api.Models;

namespace VendingMachines.Api.Services;

public sealed class SessionAdmissionGateService(AppDbContext db)
{
    public async Task<SessionAdmissionGate?> LockForSessionStartAsync(CancellationToken cancellationToken)
    {
        if (db.Database.IsNpgsql())
        {
            return await db.SessionAdmissionGates
                .FromSqlRaw("SELECT * FROM \"SessionAdmissionGates\" WHERE \"Id\" = 1 FOR SHARE")
                .SingleOrDefaultAsync(cancellationToken);
        }

        return await db.SessionAdmissionGates
            .SingleOrDefaultAsync(gate => gate.Id == SessionAdmissionGate.SingletonId, cancellationToken);
    }

    public Task<SessionAdmissionGate?> GetAsync(CancellationToken cancellationToken) =>
        db.SessionAdmissionGates.AsNoTracking()
            .SingleOrDefaultAsync(gate => gate.Id == SessionAdmissionGate.SingletonId, cancellationToken);
}
