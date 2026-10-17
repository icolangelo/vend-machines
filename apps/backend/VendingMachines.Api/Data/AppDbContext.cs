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
    public DbSet<UserInvitation> UserInvitations { get; set; } = null!;
    public DbSet<PasswordResetToken> PasswordResetTokens { get; set; } = null!;
    public DbSet<RefreshSession> RefreshSessions { get; set; } = null!;
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
    public DbSet<TelemetryCommandDispatch> TelemetryCommandDispatches { get; set; } = null!;
    public DbSet<TelemetryEvent> TelemetryEvents { get; set; } = null!;
    public DbSet<DeviceMessageInbox> DeviceMessageInbox { get; set; } = null!;
    public DbSet<DeliveryFailure> DeliveryFailures { get; set; } = null!;
    public DbSet<OutboxMessage> OutboxMessages { get; set; } = null!;
    public DbSet<AuditLog> AuditLogs { get; set; } = null!;
    public DbSet<WebhookInboxMessage> WebhookInboxMessages { get; set; } = null!;
    public DbSet<SaleRecord> SaleRecords { get; set; } = null!;
    public DbSet<MachineProductSlot> MachineProductSlots { get; set; } = null!;
    public DbSet<InventoryMovement> InventoryMovements { get; set; } = null!;
    public DbSet<OperationalAlert> OperationalAlerts { get; set; } = null!;
    public DbSet<AlertReadReceipt> AlertReadReceipts { get; set; } = null!;

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);

        // Mapeamento explícito de chaves primárias
        modelBuilder.Entity<Machine>().HasKey(m => m.Id);
        modelBuilder.Entity<Machine>().Property(machine => machine.IsActive).HasDefaultValue(true);
        modelBuilder.Entity<FullProduct>().HasKey(p => p.Id);
        modelBuilder.Entity<ProductType>().HasKey(pt => pt.Id);
        modelBuilder.Entity<ProductPerformance>().HasKey(pp => pp.Name);
        modelBuilder.Entity<User>().HasKey(u => u.Id);
        modelBuilder.Entity<User>()
            .Property(user => user.IsActive)
            .HasDefaultValue(true);
        modelBuilder.Entity<User>()
            .Property(user => user.CredentialVersion)
            .HasDefaultValue(0);
        modelBuilder.Entity<User>()
            .Property(user => user.RequiresEmailVerification)
            .HasDefaultValue(false);
        modelBuilder.Entity<User>()
            .Property(user => user.EmailVerificationTokenHash)
            .HasMaxLength(64);
        modelBuilder.Entity<User>()
            .HasIndex(user => user.EmailVerificationTokenHash)
            .IsUnique();
        modelBuilder.Entity<UserInvitation>().HasKey(invitation => invitation.Id);
        modelBuilder.Entity<UserInvitation>()
            .HasOne(invitation => invitation.Company)
            .WithMany()
            .HasForeignKey(invitation => invitation.CompanyId)
            .OnDelete(DeleteBehavior.Cascade);
        modelBuilder.Entity<UserInvitation>()
            .HasIndex(invitation => invitation.TokenHash)
            .IsUnique();
        modelBuilder.Entity<UserInvitation>().Property(invitation => invitation.Email).HasMaxLength(320);
        modelBuilder.Entity<UserInvitation>().Property(invitation => invitation.Role).HasMaxLength(32);
        modelBuilder.Entity<UserInvitation>().Property(invitation => invitation.TokenHash).HasMaxLength(64);
        modelBuilder.Entity<PasswordResetToken>().HasKey(token => token.Id);
        modelBuilder.Entity<PasswordResetToken>()
            .HasOne(token => token.User)
            .WithMany()
            .HasForeignKey(token => token.UserId)
            .OnDelete(DeleteBehavior.Cascade);
        modelBuilder.Entity<PasswordResetToken>()
            .HasIndex(token => token.TokenHash)
            .IsUnique();
        modelBuilder.Entity<PasswordResetToken>().Property(token => token.TokenHash).HasMaxLength(64);
        modelBuilder.Entity<RefreshSession>().HasKey(x => x.Id);
        modelBuilder.Entity<Company>().HasKey(c => c.Id);
        modelBuilder.Entity<Company>()
            .Property(company => company.ActivationStatus)
            .HasMaxLength(16)
            .HasDefaultValue(CompanyActivationStatuses.Pending);
        modelBuilder.Entity<Location>().HasKey(l => l.Id);
        modelBuilder.Entity<SystemSettings>().HasKey(s => s.Id);
        modelBuilder.Entity<MercadoPagoIntegration>().HasKey(mpi => mpi.Id);
        modelBuilder.Entity<PaymentTransaction>().HasKey(t => t.Id);
        modelBuilder.Entity<PaymentTransaction>().HasIndex(t => new { t.Status, t.LastReconciliationAt, t.CreatedAt });
        modelBuilder.Entity<TransactionTelemetryLog>().HasKey(tl => tl.Id);
        modelBuilder.Entity<MachineConnectionState>().HasKey(x => x.Id);
        modelBuilder.Entity<MachineSession>().HasKey(x => x.Id);
        modelBuilder.Entity<TelemetryCommand>().HasKey(x => x.Id);
        modelBuilder.Entity<TelemetryEvent>().HasKey(x => x.Id);
        modelBuilder.Entity<DeviceMessageInbox>().HasKey(x => x.Id);
        modelBuilder.Entity<DeviceMessageInbox>().HasIndex(x => x.IdempotencyKey).IsUnique();
        modelBuilder.Entity<DeviceMessageInbox>().HasIndex(x => new { x.Status, x.NextAttemptAt, x.ReceivedAt });
        modelBuilder.Entity<DeviceMessageInbox>().HasIndex(x => new { x.MachineId, x.Status, x.ReceivedAt });
        modelBuilder.Entity<DeviceMessageInbox>().Property(x => x.IdempotencyKey).HasMaxLength(64);
        modelBuilder.Entity<DeviceMessageInbox>().Property(x => x.Serial).HasMaxLength(64);
        modelBuilder.Entity<DeviceMessageInbox>().Property(x => x.ConnectionId).HasMaxLength(64);
        modelBuilder.Entity<DeviceMessageInbox>().Property(x => x.Status).HasMaxLength(16);
        modelBuilder.Entity<DeviceMessageInbox>().Property(x => x.LastError).HasMaxLength(2000);
        modelBuilder.Entity<DeliveryFailure>().HasKey(x => x.Id);
        modelBuilder.Entity<OutboxMessage>().HasKey(x => x.Id);
        modelBuilder.Entity<AuditLog>().HasKey(x => x.Id);
        modelBuilder.Entity<WebhookInboxMessage>().HasKey(x => x.Id);
        modelBuilder.Entity<WebhookInboxMessage>().HasIndex(x => x.EventKey).IsUnique();
        modelBuilder.Entity<WebhookInboxMessage>().HasIndex(x => new { x.Status, x.NextAttemptAt, x.ReceivedAt });
        modelBuilder.Entity<WebhookInboxMessage>().Property(x => x.EventKey).HasMaxLength(64);
        modelBuilder.Entity<WebhookInboxMessage>().Property(x => x.PaymentId).HasMaxLength(128);
        modelBuilder.Entity<WebhookInboxMessage>().Property(x => x.EventType).HasMaxLength(64);
        modelBuilder.Entity<WebhookInboxMessage>().Property(x => x.EventAction).HasMaxLength(128);
        modelBuilder.Entity<WebhookInboxMessage>().Property(x => x.Format).HasMaxLength(64);
        modelBuilder.Entity<WebhookInboxMessage>().Property(x => x.AuthenticationMode).HasMaxLength(32);
        modelBuilder.Entity<WebhookInboxMessage>().Property(x => x.Status).HasMaxLength(16);
        modelBuilder.Entity<WebhookInboxMessage>().Property(x => x.LastError).HasMaxLength(2000);
        modelBuilder.Entity<SaleRecord>().HasKey(x => x.Id);
        modelBuilder.Entity<SaleRecord>().HasIndex(x => x.TransactionId).IsUnique();
        modelBuilder.Entity<SaleRecord>().HasIndex(x => x.SessionId).IsUnique();
        modelBuilder.Entity<SaleRecord>().HasIndex(x => new { x.CompanyId, x.SoldAt });
        modelBuilder.Entity<SaleRecord>().HasIndex(x => new { x.CompanyId, x.ProductId, x.SoldAt });
        modelBuilder.Entity<SaleRecord>().Property(x => x.ProductNameSnapshot).HasMaxLength(256);
        modelBuilder.Entity<SaleRecord>().Property(x => x.Origin).HasMaxLength(32).HasDefaultValue("DeliveryConfirmed");
        modelBuilder.Entity<SaleRecord>().Property(x => x.FinancialStatus).HasMaxLength(32).HasDefaultValue("Settled");
        modelBuilder.Entity<SaleRecord>()
            .HasOne<Company>().WithMany().HasForeignKey(x => x.CompanyId).OnDelete(DeleteBehavior.Restrict);
        modelBuilder.Entity<SaleRecord>()
            .HasOne<Machine>().WithMany().HasForeignKey(x => x.MachineId).OnDelete(DeleteBehavior.Restrict);
        modelBuilder.Entity<SaleRecord>()
            .HasOne<MachineSession>().WithMany().HasForeignKey(x => x.SessionId).OnDelete(DeleteBehavior.Restrict);
        modelBuilder.Entity<SaleRecord>()
            .HasOne<PaymentTransaction>().WithMany().HasForeignKey(x => x.TransactionId).OnDelete(DeleteBehavior.Restrict);
        modelBuilder.Entity<SaleRecord>()
            .HasOne<FullProduct>().WithMany().HasForeignKey(x => x.ProductId).OnDelete(DeleteBehavior.Restrict);

        modelBuilder.Entity<MachineProductSlot>().HasKey(x => x.Id);
        modelBuilder.Entity<MachineProductSlot>().HasIndex(x => new { x.MachineId, x.ItemNumber }).IsUnique();
        modelBuilder.Entity<MachineProductSlot>().HasIndex(x => new { x.CompanyId, x.ProductId });
        modelBuilder.Entity<MachineProductSlot>()
            .HasOne<Company>().WithMany().HasForeignKey(x => x.CompanyId).OnDelete(DeleteBehavior.Cascade);
        modelBuilder.Entity<MachineProductSlot>()
            .HasOne<Machine>().WithMany().HasForeignKey(x => x.MachineId).OnDelete(DeleteBehavior.Cascade);
        modelBuilder.Entity<MachineProductSlot>()
            .HasOne<FullProduct>().WithMany().HasForeignKey(x => x.ProductId).OnDelete(DeleteBehavior.Restrict);

        modelBuilder.Entity<InventoryMovement>().HasKey(x => x.Id);
        modelBuilder.Entity<InventoryMovement>().HasIndex(x => new { x.MachineId, x.ItemNumber, x.CreatedAt });
        modelBuilder.Entity<InventoryMovement>().Property(x => x.MovementType).HasMaxLength(32);
        modelBuilder.Entity<InventoryMovement>().Property(x => x.Reason).HasMaxLength(500);
        modelBuilder.Entity<InventoryMovement>()
            .HasOne<MachineProductSlot>().WithMany().HasForeignKey(x => new { x.MachineId, x.ItemNumber })
            .HasPrincipalKey(x => new { x.MachineId, x.ItemNumber }).OnDelete(DeleteBehavior.Restrict);
        modelBuilder.Entity<InventoryMovement>()
            .HasOne<SaleRecord>().WithMany().HasForeignKey(x => x.SaleRecordId).OnDelete(DeleteBehavior.SetNull);

        modelBuilder.Entity<OperationalAlert>().HasKey(x => x.Id);
        modelBuilder.Entity<OperationalAlert>().HasIndex(x => new { x.CompanyId, x.Status, x.UpdatedAt });
        modelBuilder.Entity<OperationalAlert>().HasIndex(x => new { x.CompanyId, x.DeduplicationKey }).IsUnique();
        modelBuilder.Entity<OperationalAlert>().Property(x => x.DeduplicationKey).HasMaxLength(256);
        modelBuilder.Entity<OperationalAlert>().Property(x => x.Type).HasMaxLength(48);
        modelBuilder.Entity<OperationalAlert>().Property(x => x.Severity).HasMaxLength(16);
        modelBuilder.Entity<OperationalAlert>().Property(x => x.Status).HasMaxLength(16);
        modelBuilder.Entity<OperationalAlert>().Property(x => x.Title).HasMaxLength(200);
        modelBuilder.Entity<OperationalAlert>().Property(x => x.Description).HasMaxLength(1000);
        modelBuilder.Entity<OperationalAlert>().Property(x => x.Resolution).HasMaxLength(500);
        modelBuilder.Entity<OperationalAlert>()
            .HasOne<Company>().WithMany().HasForeignKey(x => x.CompanyId).OnDelete(DeleteBehavior.Cascade);
        modelBuilder.Entity<AlertReadReceipt>().HasKey(x => new { x.AlertId, x.UserId });
        modelBuilder.Entity<AlertReadReceipt>()
            .HasOne<OperationalAlert>().WithMany().HasForeignKey(x => x.AlertId).OnDelete(DeleteBehavior.Cascade);
        modelBuilder.Entity<AlertReadReceipt>()
            .HasOne<User>().WithMany().HasForeignKey(x => x.UserId).OnDelete(DeleteBehavior.Cascade);

        // Relacionamento Empresa -> Usuários (Sócios)
        modelBuilder.Entity<User>()
            .HasOne(u => u.Company)
            .WithMany(c => c.Users)
            .HasForeignKey(u => u.CompanyId)
            .OnDelete(DeleteBehavior.SetNull);

        modelBuilder.Entity<RefreshSession>()
            .HasOne(x => x.User)
            .WithMany()
            .HasForeignKey(x => x.UserId)
            .OnDelete(DeleteBehavior.Cascade);
        modelBuilder.Entity<RefreshSession>()
            .HasIndex(x => x.TokenHash)
            .IsUnique();
        modelBuilder.Entity<RefreshSession>()
            .HasIndex(x => new { x.FamilyId, x.RevokedAt });
        modelBuilder.Entity<RefreshSession>()
            .HasIndex(x => new { x.UserId, x.ExpiresAt });
        modelBuilder.Entity<RefreshSession>()
            .Property(x => x.TokenHash)
            .HasMaxLength(64);
        modelBuilder.Entity<RefreshSession>()
            .Property(x => x.CreatedByIp)
            .HasMaxLength(64);
        modelBuilder.Entity<RefreshSession>()
            .Property(x => x.UserAgent)
            .HasMaxLength(512);

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
        modelBuilder.Entity<Machine>()
            .Property(m => m.TelemetryCredentialHash)
            .HasMaxLength(64);

        modelBuilder.Entity<MachineConnectionState>()
            .HasOne(x => x.Machine)
            .WithMany()
            .HasForeignKey(x => x.MachineId)
            .OnDelete(DeleteBehavior.Cascade);
        modelBuilder.Entity<MachineConnectionState>().HasIndex(x => x.MachineId).IsUnique();
        modelBuilder.Entity<MachineConnectionState>().HasIndex(x => new { x.CompanyId, x.IsOnline });
        modelBuilder.Entity<MachineConnectionState>().Property(x => x.OwnerNodeId).HasMaxLength(32);
        modelBuilder.Entity<MachineConnectionState>().Property(x => x.OwnerConnectionId).HasMaxLength(32);

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
        modelBuilder.Entity<TelemetryCommandDispatch>().Property(x => x.OwnerNodeId).HasMaxLength(32);
        modelBuilder.Entity<TelemetryCommandDispatch>().Property(x => x.Operation).HasMaxLength(24).HasDefaultValue("Command");
        modelBuilder.Entity<TelemetryCommandDispatch>().Property(x => x.TimeoutMilliseconds).HasDefaultValue(15000);
        modelBuilder.Entity<TelemetryCommandDispatch>().Property(x => x.Status).HasMaxLength(24);
        modelBuilder.Entity<TelemetryCommandDispatch>().Property(x => x.Error).HasMaxLength(100);
        modelBuilder.Entity<TelemetryCommandDispatch>().Property(x => x.ResponseStatus).HasMaxLength(50);
        modelBuilder.Entity<TelemetryCommandDispatch>().HasIndex(x => new { x.OwnerNodeId, x.Status, x.CreatedAt });
        modelBuilder.Entity<TelemetryEvent>().HasIndex(x => new { x.CompanyId, x.MachineId, x.CreatedAt });
        modelBuilder.Entity<DeliveryFailure>().HasIndex(x => x.TransactionId).IsUnique();
        modelBuilder.Entity<OutboxMessage>().HasIndex(x => new { x.Status, x.NextAttemptAt });
        modelBuilder.Entity<AuditLog>().HasIndex(x => new { x.CompanyId, x.CreatedAt });
        modelBuilder.Entity<AuditLog>().HasIndex(x => new { x.ActorUserId, x.CreatedAt });
        modelBuilder.Entity<AuditLog>().Property(x => x.Action).HasMaxLength(100);
        modelBuilder.Entity<AuditLog>().Property(x => x.ResourceType).HasMaxLength(100);
        modelBuilder.Entity<AuditLog>().Property(x => x.ResourceId).HasMaxLength(200);
        modelBuilder.Entity<AuditLog>().Property(x => x.IpAddress).HasMaxLength(64);
        modelBuilder.Entity<AuditLog>().Property(x => x.UserAgent).HasMaxLength(512);

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

        modelBuilder.Entity<User>()
            .HasIndex(user => user.NormalizedEmail)
            .IsUnique()
            .HasFilter("\"NormalizedEmail\" <> ''");
        modelBuilder.Entity<User>()
            .HasIndex(user => user.NormalizedCpf)
            .IsUnique()
            .HasFilter("\"NormalizedCpf\" <> ''");
        modelBuilder.Entity<Company>()
            .HasIndex(company => company.NormalizedCnpj)
            .IsUnique()
            .HasFilter("\"NormalizedCnpj\" <> ''");

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
