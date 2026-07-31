using Microsoft.EntityFrameworkCore;
using VendingMachines.Api.Models;

namespace VendingMachines.Api.Data;

public class AppDbContext : DbContext
{
    public AppDbContext(DbContextOptions<AppDbContext> options) : base(options)
    {
    }

    public DbSet<Machine> Machines { get; set; } = null!;
    public DbSet<FullProduct> Products { get; set; } = null!;
    public DbSet<ProductType> ProductTypes { get; set; } = null!;
    public DbSet<ProductPerformance> ProductPerformances { get; set; } = null!;
    public DbSet<User> Users { get; set; } = null!;
    public DbSet<Company> Companies { get; set; } = null!;
    public DbSet<Location> Locations { get; set; } = null!;
    
    // Novas tabelas Mercado Pago
    public DbSet<SystemSettings> SystemSettings { get; set; } = null!;
    public DbSet<MercadoPagoIntegration> MercadoPagoIntegrations { get; set; } = null!;
    public DbSet<PaymentTransaction> PaymentTransactions { get; set; } = null!;
    public DbSet<TransactionTelemetryLog> TransactionTelemetryLogs { get; set; } = null!;
    public DbSet<MachineConnectionState> MachineConnectionStates { get; set; } = null!;
    public DbSet<MachineSession> MachineSessions { get; set; } = null!;
    public DbSet<TelemetryCommand> TelemetryCommands { get; set; } = null!;
    public DbSet<TelemetryEvent> TelemetryEvents { get; set; } = null!;
    public DbSet<DeliveryFailure> DeliveryFailures { get; set; } = null!;
    public DbSet<OutboxMessage> OutboxMessages { get; set; } = null!;

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);

        // Mapeamento explícito de chaves primárias
        modelBuilder.Entity<Machine>().HasKey(m => m.Id);
        modelBuilder.Entity<FullProduct>().HasKey(p => p.Id);
        modelBuilder.Entity<ProductType>().HasKey(pt => pt.Id);
        modelBuilder.Entity<ProductPerformance>().HasKey(pp => pp.Name);
        modelBuilder.Entity<User>().HasKey(u => u.Id);
        modelBuilder.Entity<Company>().HasKey(c => c.Id);
        modelBuilder.Entity<Location>().HasKey(l => l.Id);
        modelBuilder.Entity<SystemSettings>().HasKey(s => s.Id);
        modelBuilder.Entity<MercadoPagoIntegration>().HasKey(mpi => mpi.Id);
        modelBuilder.Entity<PaymentTransaction>().HasKey(t => t.Id);
        modelBuilder.Entity<TransactionTelemetryLog>().HasKey(tl => tl.Id);
        modelBuilder.Entity<MachineConnectionState>().HasKey(x => x.Id);
        modelBuilder.Entity<MachineSession>().HasKey(x => x.Id);
        modelBuilder.Entity<TelemetryCommand>().HasKey(x => x.Id);
        modelBuilder.Entity<TelemetryEvent>().HasKey(x => x.Id);
        modelBuilder.Entity<DeliveryFailure>().HasKey(x => x.Id);
        modelBuilder.Entity<OutboxMessage>().HasKey(x => x.Id);

        // Relacionamento Empresa -> Usuários (Sócios)
        modelBuilder.Entity<User>()
            .HasOne(u => u.Company)
            .WithMany(c => c.Users)
            .HasForeignKey(u => u.CompanyId)
            .OnDelete(DeleteBehavior.SetNull);

        // Relacionamento Empresa -> Máquinas
        modelBuilder.Entity<Machine>()
            .HasOne(m => m.Company)
            .WithMany(c => c.Machines)
            .HasForeignKey(m => m.CompanyId)
            .OnDelete(DeleteBehavior.SetNull);

        // Relacionamento Empresa -> Localizações
        modelBuilder.Entity<Location>()
            .HasOne(l => l.Company)
            .WithMany(c => c.Locations)
            .HasForeignKey(l => l.CompanyId)
            .OnDelete(DeleteBehavior.Cascade);

        // Relacionamento Empresa -> Produtos
        modelBuilder.Entity<FullProduct>()
            .HasOne(p => p.Company)
            .WithMany()
            .HasForeignKey(p => p.CompanyId)
            .OnDelete(DeleteBehavior.Cascade);

        // Relacionamento Empresa -> Tipos de Produto
        modelBuilder.Entity<ProductType>()
            .HasOne(pt => pt.Company)
            .WithMany()
            .HasForeignKey(pt => pt.CompanyId)
            .OnDelete(DeleteBehavior.Cascade);

        modelBuilder.Entity<Location>()
            .HasIndex(l => new { l.CompanyId, l.Name })
            .IsUnique();

        // Relacionamento Localização -> Máquinas
        modelBuilder.Entity<Machine>()
            .HasOne(m => m.AssignedLocation)
            .WithMany(l => l.Machines)
            .HasForeignKey(m => m.LocationId)
            .OnDelete(DeleteBehavior.Restrict);

        modelBuilder.Entity<Machine>()
            .HasIndex(m => m.NormalizedSerialNumber)
            .IsUnique();

        modelBuilder.Entity<MachineConnectionState>()
            .HasOne(x => x.Machine)
            .WithMany()
            .HasForeignKey(x => x.MachineId)
            .OnDelete(DeleteBehavior.Cascade);
        modelBuilder.Entity<MachineConnectionState>().HasIndex(x => x.MachineId).IsUnique();
        modelBuilder.Entity<MachineConnectionState>().HasIndex(x => new { x.CompanyId, x.IsOnline });

        modelBuilder.Entity<MachineSession>()
            .HasOne(x => x.Machine)
            .WithMany()
            .HasForeignKey(x => x.MachineId)
            .OnDelete(DeleteBehavior.Cascade);
        modelBuilder.Entity<MachineSession>()
            .HasOne(x => x.Transaction)
            .WithMany()
            .HasForeignKey(x => x.TransactionId)
            .OnDelete(DeleteBehavior.SetNull);
        modelBuilder.Entity<MachineSession>().HasIndex(x => new { x.CompanyId, x.MachineId, x.State });
        modelBuilder.Entity<MachineSession>().HasIndex(x => x.TransactionId).IsUnique();
        modelBuilder.Entity<MachineSession>()
            .HasIndex(x => x.MachineId)
            .IsUnique()
            .HasFilter("\"ClosedAt\" IS NULL")
            .HasDatabaseName("UX_MachineSessions_OneOpenPerMachine");

        modelBuilder.Entity<TelemetryCommand>().HasIndex(x => new { x.CompanyId, x.MachineId, x.CreatedAt });
        modelBuilder.Entity<TelemetryEvent>().HasIndex(x => new { x.CompanyId, x.MachineId, x.CreatedAt });
        modelBuilder.Entity<DeliveryFailure>().HasIndex(x => x.TransactionId).IsUnique();
        modelBuilder.Entity<OutboxMessage>().HasIndex(x => new { x.Status, x.NextAttemptAt });

        // Relacionamento Empresa -> Integração Mercado Pago
        modelBuilder.Entity<MercadoPagoIntegration>()
            .HasOne(mpi => mpi.Company)
            .WithMany()
            .HasForeignKey(mpi => mpi.CompanyId)
            .OnDelete(DeleteBehavior.Cascade);

        // Relacionamento Transações
        modelBuilder.Entity<PaymentTransaction>()
            .HasOne(t => t.Machine)
            .WithMany()
            .HasForeignKey(t => t.MachineId)
            .OnDelete(DeleteBehavior.Cascade);

        modelBuilder.Entity<PaymentTransaction>()
            .HasOne(t => t.Company)
            .WithMany()
            .HasForeignKey(t => t.CompanyId)
            .OnDelete(DeleteBehavior.Cascade);

        // Relacionamento Logs
        modelBuilder.Entity<TransactionTelemetryLog>()
            .HasOne(tl => tl.Transaction)
            .WithMany(t => t.TelemetryLogs)
            .HasForeignKey(tl => tl.TransactionId)
            .OnDelete(DeleteBehavior.Cascade);

        // Índice único no e-mail
        modelBuilder.Entity<User>()
            .HasIndex(u => u.Email)
            .IsUnique();

        // Configurações adicionais de precisão
        modelBuilder.Entity<Machine>()
            .Property(m => m.Revenue30d)
            .HasPrecision(18, 2);

        modelBuilder.Entity<FullProduct>()
            .Property(p => p.Cost)
            .HasPrecision(18, 3);

        modelBuilder.Entity<ProductPerformance>()
            .Property(pp => pp.Revenue)
            .HasPrecision(18, 2);

        modelBuilder.Entity<SystemSettings>()
            .Property(s => s.ApplicationFeePercent)
            .HasPrecision(5, 2);

        modelBuilder.Entity<PaymentTransaction>()
            .Property(t => t.Amount)
            .HasPrecision(18, 2);

        modelBuilder.Entity<PaymentTransaction>()
            .Property(t => t.ApplicationFee)
            .HasPrecision(18, 2);
    }
}
