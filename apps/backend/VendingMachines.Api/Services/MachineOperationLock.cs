using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using VendingMachines.Api.Data;

namespace VendingMachines.Api.Services;

public static class MachineOperationLock
{
    public static async Task<bool> AcquireAsync(
        AppDbContext db,
        string machineId,
        Guid companyId,
        CancellationToken cancellationToken)
    {
        var transaction = db.Database.CurrentTransaction
            ?? throw new InvalidOperationException("A transação da operação da máquina não foi iniciada.");

        if (!db.Database.IsNpgsql())
        {
            return await db.Machines.AsNoTracking().AnyAsync(
                machine => machine.Id == machineId && machine.CompanyId == companyId,
                cancellationToken);
        }

        var connection = db.Database.GetDbConnection();
        await using var command = connection.CreateCommand();
        command.Transaction = transaction.GetDbTransaction();
        command.CommandTimeout = 5;
        command.CommandText = "SELECT \"Id\" FROM \"Machines\" WHERE \"Id\" = @machineId AND \"CompanyId\" = @companyId FOR NO KEY UPDATE";

        var machineIdParameter = command.CreateParameter();
        machineIdParameter.ParameterName = "machineId";
        machineIdParameter.Value = machineId;
        command.Parameters.Add(machineIdParameter);

        var companyIdParameter = command.CreateParameter();
        companyIdParameter.ParameterName = "companyId";
        companyIdParameter.Value = companyId;
        command.Parameters.Add(companyIdParameter);

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken);
    }
}
