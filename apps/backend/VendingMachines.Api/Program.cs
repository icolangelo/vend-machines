using Microsoft.EntityFrameworkCore;
using VendingMachines.Api.Data;
using VendingMachines.Api.Services;

var builder = WebApplication.CreateBuilder(args);

// Add services to the container.
builder.Services.AddControllers();

// Configure DbContext with PostgreSQL or SQLite fallback
var connectionString = builder.Configuration.GetConnectionString("DefaultConnection");
var useSqlite = builder.Configuration.GetValue<bool>("UseSqlite") || 
                string.IsNullOrEmpty(connectionString) || 
                connectionString.Contains("YOUR_NEON_HOST") ||
                connectionString.Contains("Data Source", StringComparison.OrdinalIgnoreCase) ||
                connectionString.Contains(".db", StringComparison.OrdinalIgnoreCase) ||
                connectionString.Contains("sqlite", StringComparison.OrdinalIgnoreCase);

builder.Services.AddDbContext<AppDbContext>(options =>
{
    if (useSqlite)
    {
        var sqliteConn = (connectionString != null && (connectionString.Contains("Data Source", StringComparison.OrdinalIgnoreCase) || connectionString.Contains(".db", StringComparison.OrdinalIgnoreCase)))
            ? connectionString
            : (builder.Configuration.GetConnectionString("SqliteConnection") ?? "Data Source=vending.db");
        options.UseSqlite(sqliteConn);
    }
    else
    {
        options.UseNpgsql(connectionString);
    }
});

// Register Database Data Service
builder.Services.AddScoped<IDataService, DatabaseDataService>();

// Configure Swagger
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen();

// Configure CORS
builder.Services.AddCors(options =>
{
    options.AddDefaultPolicy(policy =>
    {
        policy.AllowAnyOrigin()
              .AllowAnyMethod()
              .AllowAnyHeader();
    });
});

// Register Telemetry Manager for Raw WebSockets
builder.Services.AddSingleton<TelemetryManager>();

var app = builder.Build();

// Seed Database
using (var scope = app.Services.CreateScope())
{
    var services = scope.ServiceProvider;
    try
    {
        var context = services.GetRequiredService<AppDbContext>();
        DbInitializer.Initialize(context);
    }
    catch (Exception ex)
    {
        var logger = services.GetRequiredService<ILogger<Program>>();
        logger.LogError(ex, "Ocorreu um erro ao inicializar/semear o banco de dados.");
    }
}

// Configure the HTTP request pipeline.
if (app.Environment.IsDevelopment())
{
    app.UseSwagger();
    app.UseSwaggerUI();
}

app.UseCors();
app.UseWebSockets();
app.UseMiddleware<VendingMachines.Api.Middlewares.WebSocketMiddleware>();

app.UseAuthorization();

app.MapControllers();

app.Run();
