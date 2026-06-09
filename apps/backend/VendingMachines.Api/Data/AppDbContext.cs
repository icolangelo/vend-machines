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

        // Índice único no e-mail
        modelBuilder.Entity<User>()
            .HasIndex(u => u.Email)
            .IsUnique();

        // Configurações adicionais de precisão/tipo de dados para PostgreSQL (opcional, mas boa prática)
        modelBuilder.Entity<Machine>()
            .Property(m => m.Revenue30d)
            .HasPrecision(18, 2);

        modelBuilder.Entity<FullProduct>()
            .Property(p => p.Cost)
            .HasPrecision(18, 3);

        modelBuilder.Entity<ProductPerformance>()
            .Property(pp => pp.Revenue)
            .HasPrecision(18, 2);
    }
}
