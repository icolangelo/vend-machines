using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.EntityFrameworkCore;
using VendingMachines.Api.Models;

namespace VendingMachines.Api.Data;

public static class DbInitializer
{
    public static void Initialize(AppDbContext context)
    {
        // Executar migrations automáticas se necessário ou apenas garantir a criação para SQLite
        if (context.Database.IsSqlite())
        {
            context.Database.EnsureCreated();
        }
        else
        {
            context.Database.Migrate();
        }

        // Definição de IDs fixos para garantir relacionamentos consistentes
        var companyId = Guid.Parse("11111111-1111-1111-1111-111111111111");
        var adminUserId = Guid.Parse("22222222-2222-2222-2222-222222222222");
        var joaoUserId = Guid.Parse("33333333-3333-3333-3333-333333333333");

        // 0. Seed de SystemSettings
        if (!context.SystemSettings.Any())
        {
            var defaultSettings = new SystemSettings
            {
                Id = Guid.NewGuid(),
                ApplicationFeePercent = 5.0m,
                UpdatedAt = DateTime.UtcNow
            };
            context.SystemSettings.Add(defaultSettings);
        }

        // 1. Seed de Companies
        if (!context.Companies.Any())
        {
            var defaultCompany = new Company
            {
                Id = companyId,
                Name = "ACME Machines LTDA",
                CreatedByUserId = adminUserId,
                CreatedAt = DateTime.UtcNow,
                Cnpj = "12.345.678/0001-99"
            };
            context.Companies.Add(defaultCompany);
        }

        // 2. Seed de ProductTypes
        if (!context.ProductTypes.Any())
        {
            var productTypes = new List<ProductType>
            {
                new() { Id = "pt-1", Name = "Bebidas Frias" },
                new() { Id = "pt-2", Name = "Bebidas Quentes" },
                new() { Id = "pt-3", Name = "Carne" },
                new() { Id = "pt-4", Name = "Doces" },
                new() { Id = "pt-5", Name = "Outros" },
                new() { Id = "pt-6", Name = "Refeição" },
                new() { Id = "pt-7", Name = "Sanduíche" },
                new() { Id = "pt-8", Name = "Snacks" },
                new() { Id = "pt-9", Name = "Yogurt" }
            };
            context.ProductTypes.AddRange(productTypes);
        }

        // 3. Seed de Machines
        if (!context.Machines.Any())
        {
            var machines = new List<Machine>
            {
                new() { Id = "VM-001", Name = "Lobby A1", ClientName = "Hospital São Luiz", Location = "Lobby Principal", Status = "online", StockLevel = 82, Revenue30d = 4230m, TotalSales30d = 847, LastSync = "2 min atrás", SerialNumber = "SN-123456", CompanyId = companyId },
                new() { Id = "VM-002", Name = "Refeitório B2", ClientName = "Hospital São Luiz", Location = "Refeitório 2º andar", Status = "warning", StockLevel = 18, Revenue30d = 3890m, TotalSales30d = 778, LastSync = "5 min atrás", SerialNumber = "SN-987654", CompanyId = companyId },
                new() { Id = "VM-003", Name = "UTI C1", ClientName = "Hospital São Luiz", Location = "Corredor UTI", Status = "online", StockLevel = 65, Revenue30d = 2150m, TotalSales30d = 430, LastSync = "1 min atrás", SerialNumber = "SN-555555", CompanyId = companyId },
                new() { Id = "VM-004", Name = "Recepção D1", ClientName = "Faculdade Anhanguera", Location = "Bloco D", Status = "online", StockLevel = 91, Revenue30d = 5670m, TotalSales30d = 1134, LastSync = "3 min atrás", SerialNumber = "SN-004", CompanyId = companyId },
                new() { Id = "VM-005", Name = "Cantina E1", ClientName = "Faculdade Anhanguera", Location = "Cantina Central", Status = "offline", StockLevel = 0, Revenue30d = 0m, TotalSales30d = 0, LastSync = "2 dias atrás", SerialNumber = "SN-005", CompanyId = companyId },
                new() { Id = "VM-006", Name = "Hall F1", ClientName = "Condomínio Alphaville", Location = "Hall de Entrada", Status = "online", StockLevel = 45, Revenue30d = 1890m, TotalSales30d = 378, LastSync = "4 min atrás", SerialNumber = "SN-006", CompanyId = companyId },
                new() { Id = "VM-007", Name = "Academia G1", ClientName = "Condomínio Alphaville", Location = "Academia", Status = "warning", StockLevel = 12, Revenue30d = 3420m, TotalSales30d = 684, LastSync = "8 min atrás", SerialNumber = "SN-007", CompanyId = companyId },
                new() { Id = "VM-008", Name = "Terminal H1", ClientName = "Rodoviária Tietê", Location = "Terminal 3", Status = "online", StockLevel = 73, Revenue30d = 8940m, TotalSales30d = 1788, LastSync = "1 min atrás", SerialNumber = "SN-008", CompanyId = companyId },
                new() { Id = "VM-009", Name = "Embarque I1", ClientName = "Rodoviária Tietê", Location = "Sala de Embarque", Status = "online", StockLevel = 56, Revenue30d = 7230m, TotalSales30d = 1446, LastSync = "2 min atrás", SerialNumber = "SN-009", CompanyId = companyId },
                new() { Id = "VM-010", Name = "Plataforma J1", ClientName = "Rodoviária Tietê", Location = "Plataforma 12", Status = "warning", StockLevel = 22, Revenue30d = 6180m, TotalSales30d = 1236, LastSync = "15 min atrás", SerialNumber = "SN-010", CompanyId = companyId },
                new() { Id = "VM-011", Name = "Escritório K1", ClientName = "WeWork Faria Lima", Location = "12º andar", Status = "online", StockLevel = 88, Revenue30d = 4560m, TotalSales30d = 912, LastSync = "1 min atrás", SerialNumber = "SN-011", CompanyId = companyId },
                new() { Id = "VM-012", Name = "Lounge L1", ClientName = "WeWork Faria Lima", Location = "Lounge Café", Status = "online", StockLevel = 71, Revenue30d = 5230m, TotalSales30d = 1046, LastSync = "3 min atrás", SerialNumber = "SN-012", CompanyId = companyId }
            };
            context.Machines.AddRange(machines);
        }
        else
        {
            var defaultCompany = context.Companies.FirstOrDefault();
            if (defaultCompany != null)
            {
                var unlinkedMachines = context.Machines.Where(m => m.CompanyId == null).ToList();
                if (unlinkedMachines.Any())
                {
                    foreach (var m in unlinkedMachines)
                    {
                        m.CompanyId = defaultCompany.Id;
                    }
                    context.SaveChanges();
                }
            }
        }

        // 4. Seed de Products (FullProduct)
        if (!context.Products.Any())
        {
            var products = new List<FullProduct>
            {
                new() { Id = "p-1", Code = "BEB-001", Name = "Coca-Cola Lata", Description = "Refrigerante de cola 350ml", TypeId = "pt-1", IsAlcoholic = false, Cost = 2.500m },
                new() { Id = "p-2", Code = "ALC-001", Name = "Cerveja Heineken", Description = "Cerveja lager premium 330ml", TypeId = "pt-1", IsAlcoholic = true, Cost = 4.850m },
                new() { Id = "p-3", Code = "SNA-001", Name = "Batata Pringles", Description = "Batata frita sabor original 114g", TypeId = "pt-8", IsAlcoholic = false, Cost = 12.300m }
            };
            context.Products.AddRange(products);
        }

        // 5. Seed de ProductPerformances
        if (!context.ProductPerformances.Any())
        {
            var performances = new List<ProductPerformance>
            {
                // Top Products
                new() { Name = "Água Mineral 500ml", TotalSold = 3240, Revenue = 9720m, Trend = 12.3, IsTop = true },
                new() { Name = "Coca-Cola Lata", TotalSold = 2890, Revenue = 14450m, Trend = 8.1, IsTop = true },
                new() { Name = "Barra de Cereal", TotalSold = 1670, Revenue = 5845m, Trend = 5.4, IsTop = true },
                new() { Name = "Suco Del Valle", TotalSold = 1420, Revenue = 7100m, Trend = -2.1, IsTop = true },
                new() { Name = "Biscoito Oreo", TotalSold = 1180, Revenue = 4720m, Trend = 15.7, IsTop = true },
                
                // Bottom Products
                new() { Name = "Chá Gelado Leão", TotalSold = 89, Revenue = 356m, Trend = -28.4, IsTop = false },
                new() { Name = "Amendoim Japonês", TotalSold = 112, Revenue = 336m, Trend = -19.2, IsTop = false },
                new() { Name = "Energético Monster", TotalSold = 156, Revenue = 1248m, Trend = -12.8, IsTop = false },
                new() { Name = "Paçoca Amor", TotalSold = 178, Revenue = 356m, Trend = -8.5, IsTop = false },
                new() { Name = "Vitamina Yakult", TotalSold = 203, Revenue = 609m, Trend = -5.1, IsTop = false }
            };
            context.ProductPerformances.AddRange(performances);
        }

        // 6. Seed de Users (Dono/Criador e Sócio)
        if (!context.Users.Any())
        {
            var hasher = new Microsoft.AspNetCore.Identity.PasswordHasher<User>();
            
            var adminUser = new User
            {
                Id = adminUserId,
                Name = "Admin Ivan",
                Email = "dev.ivan@gmail.com",
                Role = "Admin",
                CompanyId = companyId,
                CreatedAt = DateTime.UtcNow,
                Cpf = "123.456.789-00",
                AcceptedPrivacyPolicy = true,
                PrivacyPolicyAcceptedAt = DateTime.UtcNow
            };
            adminUser.PasswordHash = hasher.HashPassword(adminUser, "admin123");
            context.Users.Add(adminUser);

            var partnerUser = new User
            {
                Id = joaoUserId,
                Name = "João da Silva",
                Email = "joao@vmmanager.com",
                Role = "Admin",
                CompanyId = companyId,
                CreatedAt = DateTime.UtcNow,
                Cpf = "987.654.321-99",
                AcceptedPrivacyPolicy = true,
                PrivacyPolicyAcceptedAt = DateTime.UtcNow
            };
            partnerUser.PasswordHash = hasher.HashPassword(partnerUser, "teste123");
            context.Users.Add(partnerUser);
        }

        context.SaveChanges();
    }
}
