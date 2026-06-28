using System;
using System.Linq;
using Microsoft.EntityFrameworkCore;
using VendingMachines.Api.Data;

var options = new DbContextOptionsBuilder<AppDbContext>().UseSqlite("Data Source=vending_machines.db").Options;
using var db = new AppDbContext(options);

var types = db.ProductTypes.ToList();
Console.WriteLine("PRODUCT TYPES:");
foreach(var t in types) {
    Console.WriteLine($"{t.Id} | {t.Name} | {t.CompanyId} | {t.OriginalId}");
}

var prods = db.Products.ToList();
Console.WriteLine("PRODUCTS:");
foreach(var p in prods) {
    Console.WriteLine($"{p.Id} | {p.Name} | {p.TypeId} | {p.CompanyId} | {p.OriginalId}");
}
