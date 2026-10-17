using VendingMachines.Api.Services;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using VendingMachines.Api.Data;
using VendingMachines.Api.Models;
using Xunit;

namespace VendingMachines.Api.Tests;

public sealed class RegistrationIdentityTests
{
    [Fact]
    public void EmailNormalizationTrimsAndIgnoresCase()
    {
        Assert.Equal("financeiro@empresa.com.br", RegistrationIdentity.NormalizeEmail(" Financeiro@Empresa.com.br "));
    }

    [Theory]
    [InlineData("529.982.247-25")]
    [InlineData("52998224725")]
    public void AcceptsValidCpfFormats(string cpf)
    {
        Assert.True(RegistrationIdentity.IsValidCpf(cpf));
        Assert.Equal("52998224725", RegistrationIdentity.NormalizeCpf(cpf));
    }

    [Theory]
    [InlineData("111.111.111-11")]
    [InlineData("529.982.247-24")]
    [InlineData("1234567890")]
    public void RejectsInvalidCpf(string cpf) =>
        Assert.False(RegistrationIdentity.IsValidCpf(cpf));

    [Theory]
    [InlineData("12.345.678/0001-95", "12345678000195")]
    [InlineData("12.ABC.345/01DE-35", "12ABC34501DE35")]
    public void AcceptsNumericAndAlphanumericCnpj(string cnpj, string normalized)
    {
        Assert.True(RegistrationIdentity.IsValidCnpj(cnpj));
        Assert.Equal(normalized, RegistrationIdentity.NormalizeCnpj(cnpj));
    }

    [Theory]
    [InlineData("11.111.111/1111-11")]
    [InlineData("12.ABC.345/01DE-36")]
    [InlineData("12.ABC.345/01D!-35")]
    [InlineData("12.ABC.345/01DE-3A")]
    public void RejectsInvalidCnpj(string cnpj) =>
        Assert.False(RegistrationIdentity.IsValidCnpj(cnpj));

    [Fact]
    public async Task CanonicalEmailCpfAndCnpjAreUniqueAtDatabaseLevel()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseSqlite(connection)
            .Options;
        await using var db = new AppDbContext(options);
        await db.Database.EnsureCreatedAsync();

        db.Companies.Add(new Company
        {
            Name = "Empresa original",
            Cnpj = "12.ABC.345/01DE-35",
            NormalizedCnpj = RegistrationIdentity.NormalizeCnpj("12.ABC.345/01DE-35")
        });
        await db.SaveChangesAsync();
        db.Companies.Add(new Company
        {
            Name = "Empresa duplicada",
            Cnpj = "12ABC34501DE35",
            NormalizedCnpj = RegistrationIdentity.NormalizeCnpj("12ABC34501DE35")
        });
        await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync());

        db.ChangeTracker.Clear();
        db.Users.Add(new User
        {
            Name = "Usuário original",
            Email = "Person@Example.com",
            NormalizedEmail = RegistrationIdentity.NormalizeEmail("Person@Example.com"),
            Cpf = "529.982.247-25",
            NormalizedCpf = RegistrationIdentity.NormalizeCpf("529.982.247-25")
        });
        await db.SaveChangesAsync();

        db.Users.Add(new User
        {
            Name = "E-mail duplicado",
            Email = " person@example.com ",
            NormalizedEmail = RegistrationIdentity.NormalizeEmail(" person@example.com "),
            Cpf = "111.444.777-35",
            NormalizedCpf = RegistrationIdentity.NormalizeCpf("111.444.777-35")
        });
        await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync());

        db.ChangeTracker.Clear();
        db.Users.Add(new User
        {
            Name = "CPF duplicado",
            Email = "cpf-duplicado@example.com",
            NormalizedEmail = RegistrationIdentity.NormalizeEmail("cpf-duplicado@example.com"),
            Cpf = "52998224725",
            NormalizedCpf = RegistrationIdentity.NormalizeCpf("52998224725")
        });
        await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync());
    }
}
