using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.EntityFrameworkCore;
using Microsoft.IdentityModel.Tokens;
using Microsoft.OpenApi.Models;
using System.Text;
using VendingMachines.Api.Data;
using VendingMachines.Api.Services;

var builder = WebApplication.CreateBuilder(args);

// Add services to the container.
builder.Services.AddControllers();
builder.Services.AddHttpClient("MercadoPago");

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

    // Ignore pending model changes warning when dynamically switching providers between SQLite and PostgreSQL
    options.ConfigureWarnings(warnings => warnings.Ignore(Microsoft.EntityFrameworkCore.Diagnostics.RelationalEventId.PendingModelChangesWarning));
});

// Register Database Data Service
builder.Services.AddScoped<IDataService, DatabaseDataService>();

// Configure JWT Authentication
var jwtKey = builder.Configuration["Jwt:Key"] ?? "SuperSecretKeyForVendingMachinesManager2026!";
var issuer = builder.Configuration["Jwt:Issuer"] ?? "VendingMachinesApi";
var audience = builder.Configuration["Jwt:Audience"] ?? "VendingMachinesApp";

builder.Services.AddAuthentication(options =>
{
    options.DefaultAuthenticateScheme = JwtBearerDefaults.AuthenticationScheme;
    options.DefaultChallengeScheme = JwtBearerDefaults.AuthenticationScheme;
})
.AddJwtBearer(options =>
{
    options.TokenValidationParameters = new TokenValidationParameters
    {
        ValidateIssuer = true,
        ValidateAudience = true,
        ValidateLifetime = true,
        ValidateIssuerSigningKey = true,
        ValidIssuer = issuer,
        ValidAudience = audience,
        IssuerSigningKey = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(jwtKey)),
        ClockSkew = TimeSpan.Zero
    };
});

// Configure Swagger
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen(c =>
{
    c.SwaggerDoc("v1", new OpenApiInfo { Title = "Vending Machines API", Version = "v1" });
    
    c.AddSecurityDefinition("Bearer", new OpenApiSecurityScheme
    {
        Description = "Insira o token JWT neste formato: Bearer {seu_token}",
        Name = "Authorization",
        In = ParameterLocation.Header,
        Type = SecuritySchemeType.ApiKey,
        Scheme = "Bearer"
    });

    c.AddSecurityRequirement(new OpenApiSecurityRequirement
    {
        {
            new OpenApiSecurityScheme
            {
                Reference = new OpenApiReference
                {
                    Type = ReferenceType.SecurityScheme,
                    Id = "Bearer"
                }
            },
            Array.Empty<string>()
        }
    });
});

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

// Telemetria: conexões físicas em memória, estado durável no PostgreSQL/SQLite.
builder.Services.AddSingleton<WebSocketTicketService>();
builder.Services.AddSingleton<TelemetryPanelHub>();
builder.Services.AddSingleton<TelemetryConnectionManager>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<TelemetryConnectionManager>());
builder.Services.AddScoped<SessionOrchestrator>();
builder.Services.Configure<MdbStatusOptions>(builder.Configuration.GetSection("MdbStatus"));
builder.Services.AddScoped<MdbStatusRequestService>();
builder.Services.AddHostedService<TelemetryDeadlineWorker>();
builder.Services.AddHostedService<MdbStatusPollingWorker>();

var app = builder.Build();

// Seed Database
using (var scope = app.Services.CreateScope())
{
    var services = scope.ServiceProvider;
    var logger = services.GetRequiredService<ILogger<Program>>();
    try
    {
        var context = services.GetRequiredService<AppDbContext>();
        DbInitializer.Initialize(context, app.Environment.IsDevelopment());
        context.MachineConnectionStates.ExecuteUpdate(setters => setters
            .SetProperty(x => x.IsOnline, false)
            .SetProperty(x => x.ConnectionId, (string?)null)
            .SetProperty(x => x.DisconnectedAt, DateTime.UtcNow));
    }
    catch (Exception ex)
    {
        logger.LogCritical(ex, "Falha crítica ao inicializar o banco de dados. A aplicação será encerrada.");
        throw; // Impede o app de subir com banco desatualizado
    }
}

// Configure the HTTP request pipeline.
if (app.Environment.IsDevelopment())
{
    app.UseSwagger();
    app.UseSwaggerUI();
}

app.UseAuthentication();
app.UseAuthorization();
app.UseMiddleware<VendingMachines.Api.Middlewares.ImpersonationMiddleware>();

app.UseCors();
var webSocketOptions = new WebSocketOptions
{
    KeepAliveInterval = TimeSpan.FromSeconds(30),
    KeepAliveTimeout = TimeSpan.FromSeconds(30)
};
var allowedOrigins = builder.Configuration.GetSection("WebSockets:AllowedOrigins").Get<string[]>();
if (allowedOrigins is { Length: > 0 })
{
    foreach (var origin in allowedOrigins) webSocketOptions.AllowedOrigins.Add(origin);
}
app.UseWebSockets(webSocketOptions);
app.UseMiddleware<VendingMachines.Api.Middlewares.WebSocketMiddleware>();

app.MapControllers();

app.Run();
