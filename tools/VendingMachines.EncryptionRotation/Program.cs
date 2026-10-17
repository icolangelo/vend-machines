using System.Security.Cryptography;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using VendingMachines.Api.Data;
using VendingMachines.Api.Services;

const string prefix = EncryptionService.CurrentCiphertextPrefix;
var apply = args.Contains("--apply", StringComparer.Ordinal);
var confirmWrite = args.Contains("--confirm-write", StringComparer.Ordinal);
var batchSize = 100;

if (args.Contains("--help", StringComparer.Ordinal) || args.Contains("-h", StringComparer.Ordinal))
{
    PrintUsage();
    return 0;
}

for (var index = 0; index < args.Length; index++)
{
    if (args[index] == "--batch-size")
    {
        if (++index >= args.Length || !int.TryParse(args[index], out batchSize) || batchSize is < 1 or > 500)
        {
            Console.Error.WriteLine("--batch-size precisa estar entre 1 e 500.");
            return 2;
        }
    }
    else if (args[index] is not ("--apply" or "--confirm-write"))
    {
        Console.Error.WriteLine($"Opção desconhecida: {args[index]}");
        PrintUsage();
        return 2;
    }
}

if (apply && !confirmWrite)
{
    Console.Error.WriteLine("A gravação exige --apply e --confirm-write.");
    return 2;
}

var configuration = new ConfigurationBuilder().AddEnvironmentVariables().Build();
var connectionString = configuration.GetConnectionString("DefaultConnection");
var currentKey = configuration["Encryption:Key"];
var legacyKey = configuration["Encryption:LegacyKey"];
if (string.IsNullOrWhiteSpace(connectionString) || string.IsNullOrWhiteSpace(currentKey) || string.IsNullOrWhiteSpace(legacyKey))
{
    Console.Error.WriteLine("Defina ConnectionStrings__DefaultConnection, Encryption__Key e Encryption__LegacyKey no ambiente.");
    return 2;
}

try
{
    EncryptionService.Configure(
        currentKey,
        legacyKey,
        isDevelopment: false,
        writeLegacyFormat: false,
        requireLegacyData: true);
}
catch (InvalidOperationException ex)
{
    Console.Error.WriteLine(ex.Message);
    return 2;
}

var options = new DbContextOptionsBuilder<AppDbContext>()
    .UseNpgsql(connectionString)
    .Options;
await using var db = new AppDbContext(options);
if (!await db.Database.CanConnectAsync())
{
    Console.Error.WriteLine("Não foi possível conectar ao PostgreSQL configurado.");
    return 3;
}

var candidateIds = await NeedsReencryption(db.MercadoPagoIntegrations)
    .OrderBy(x => x.Id)
    .Select(x => x.Id)
    .ToListAsync();
var invalidRows = await CountUnprocessableRows(db, batchSize);
Console.WriteLine($"Integrações que ainda têm valores fora de {prefix}: {candidateIds.Count}.");
Console.WriteLine($"Integrações com valores que não podem ser migrados: {invalidRows}.");
if (invalidRows > 0)
{
    Console.Error.WriteLine("Corrija a chave atual ou investigue os ciphertexts inválidos antes de continuar.");
    return 4;
}

if (!apply)
{
    Console.WriteLine("Simulação somente leitura concluída. Nenhum registro foi alterado.");
    return 0;
}

var updated = 0;
var changedConcurrently = 0;
try
{
    foreach (var idBatch in candidateIds.Chunk(batchSize))
    {
        var rows = await db.MercadoPagoIntegrations
            .AsNoTracking()
            .Where(x => idBatch.Contains(x.Id))
            .Select(x => new { x.Id, x.AccessToken, x.RefreshToken, x.ClientSecret })
            .ToListAsync();

        foreach (var row in rows)
        {
            var accessToken = EncryptionService.Reencrypt(row.AccessToken);
            var refreshToken = EncryptionService.Reencrypt(row.RefreshToken);
            var clientSecret = EncryptionService.Reencrypt(row.ClientSecret);

            var affected = await db.MercadoPagoIntegrations
                .Where(x => x.Id == row.Id &&
                            x.AccessToken == row.AccessToken &&
                            x.RefreshToken == row.RefreshToken &&
                            x.ClientSecret == row.ClientSecret)
                .ExecuteUpdateAsync(setters => setters
                    .SetProperty(x => x.AccessToken, accessToken)
                    .SetProperty(x => x.RefreshToken, refreshToken)
                    .SetProperty(x => x.ClientSecret, clientSecret));

            if (affected == 1) updated++;
            else changedConcurrently++;
        }

        Console.WriteLine($"Lote concluído; integrações atualizadas: {updated}; alteradas em paralelo: {changedConcurrently}.");
    }
}
catch (CryptographicException)
{
    Console.Error.WriteLine("Uma integração não pôde ser recriptografada. Nenhuma coluna dessa linha foi alterada.");
    Console.Error.WriteLine("Os registros já atualizados permanecem válidos; corrija a chave legada e execute novamente.");
    return 4;
}

var remaining = await NeedsReencryption(db.MercadoPagoIntegrations).CountAsync();
var invalidAfter = await CountUnprocessableRows(db, batchSize);
Console.WriteLine($"Concluído. Atualizadas: {updated}; alteradas em paralelo: {changedConcurrently}; ainda pendentes: {remaining}; valores inválidos: {invalidAfter}.");
return remaining == 0 && invalidAfter == 0 ? 0 : 5;

static IQueryable<VendingMachines.Api.Models.MercadoPagoIntegration> NeedsReencryption(
    IQueryable<VendingMachines.Api.Models.MercadoPagoIntegration> query) =>
    query.Where(x =>
        (x.AccessToken != string.Empty && !x.AccessToken.StartsWith(EncryptionService.CurrentCiphertextPrefix)) ||
        (x.RefreshToken != string.Empty && !x.RefreshToken.StartsWith(EncryptionService.CurrentCiphertextPrefix)) ||
        (x.ClientSecret != string.Empty && !x.ClientSecret.StartsWith(EncryptionService.CurrentCiphertextPrefix)));

static async Task<int> CountUnprocessableRows(AppDbContext db, int batchSize)
{
    var ids = await db.MercadoPagoIntegrations.OrderBy(x => x.Id).Select(x => x.Id).ToListAsync();
    var invalidRows = 0;
    foreach (var idBatch in ids.Chunk(batchSize))
    {
        var rows = await db.MercadoPagoIntegrations
            .AsNoTracking()
            .Where(x => idBatch.Contains(x.Id))
            .Select(x => new { x.AccessToken, x.RefreshToken, x.ClientSecret })
            .ToListAsync();
        foreach (var row in rows)
        {
            try
            {
                _ = EncryptionService.Reencrypt(row.AccessToken);
                _ = EncryptionService.Reencrypt(row.RefreshToken);
                _ = EncryptionService.Reencrypt(row.ClientSecret);
            }
            catch (CryptographicException)
            {
                invalidRows++;
            }
        }
    }
    return invalidRows;
}

static void PrintUsage() => Console.WriteLine("""
    Uso:
      dotnet run --project tools/VendingMachines.EncryptionRotation -- [--batch-size 100]
      dotnet run --project tools/VendingMachines.EncryptionRotation -- --apply --confirm-write [--batch-size 100]

    Sem --apply, a ferramenta somente conta e verifica candidatos.
    A aplicação exige confirmação explícita, backup validado e as variáveis PostgreSQL/cifra no ambiente.
    """);
