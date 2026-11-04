using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.EntityFrameworkCore;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.IdentityModel.Tokens;
using Microsoft.OpenApi;
using System.Text;
using System.Threading.RateLimiting;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.Extensions.Hosting;
using Npgsql;
using VendingMachines.Api.Data;
using VendingMachines.Api.Services;

var builder = WebApplication.CreateBuilder(args);
var passiveDeploymentMode = builder.Configuration.GetValue<bool>("Deployment:PassiveMode");
var aspNetCoreEnvironment = Environment.GetEnvironmentVariable("ASPNETCORE_ENVIRONMENT")?.Trim();
var dotnetEnvironment = Environment.GetEnvironmentVariable("DOTNET_ENVIRONMENT")?.Trim();
var rejectDevelopmentEnvironment = builder.Configuration.GetValue<bool>("VendingMachines:RejectDevelopmentEnvironment");
if (rejectDevelopmentEnvironment &&
    !string.IsNullOrWhiteSpace(aspNetCoreEnvironment) &&
    !string.IsNullOrWhiteSpace(dotnetEnvironment) &&
    !string.Equals(aspNetCoreEnvironment, dotnetEnvironment, StringComparison.OrdinalIgnoreCase))
{
    throw new InvalidOperationException(
        "ASPNETCORE_ENVIRONMENT e DOTNET_ENVIRONMENT devem indicar o mesmo ambiente na imagem de runtime da API.");
}

var developmentEnvironmentRequested = builder.Environment.IsDevelopment() ||
    IsDevelopmentEnvironment(aspNetCoreEnvironment) ||
    IsDevelopmentEnvironment(dotnetEnvironment);
if (rejectDevelopmentEnvironment && developmentEnvironmentRequested)
{
    throw new InvalidOperationException(
        "A imagem de runtime da API não pode iniciar no ambiente Development; use um container local de desenvolvimento separado.");
}

builder.WebHost.ConfigureKestrel(SecurityHeadersConfiguration.ConfigureKestrel);
builder.Services.Configure<HostOptions>(options =>
    options.ShutdownTimeout = TimeSpan.FromSeconds(GracefulShutdownBudget.HostTimeoutSeconds));

var forwardedHeadersEnabled = TrustedProxyForwarding.IsEnabled(builder.Configuration);
var forwardedHeadersOptions = forwardedHeadersEnabled
    ? TrustedProxyForwarding.CreateOptions(builder.Configuration)
    : null;

EncryptionService.Configure(
    builder.Configuration["Encryption:Key"],
    builder.Configuration["Encryption:LegacyKey"],
    builder.Environment.IsDevelopment(),
    builder.Configuration.GetValue("Encryption:WriteLegacyFormat", !builder.Environment.IsDevelopment()),
    builder.Configuration.GetValue("Encryption:RequireLegacyData", !builder.Environment.IsDevelopment()));

// Add services to the container.
builder.Services.AddControllers();
builder.Services.AddHttpContextAccessor();
builder.Services.AddScoped<IAuditLogService, AuditLogService>();
builder.Services.AddRateLimiter(options =>
{
    options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
    options.AddPolicy("auth", context => RateLimitPartition.GetFixedWindowLimiter(
        GetRateLimitPartition(context),
        _ => new FixedWindowRateLimiterOptions
        {
            PermitLimit = 12,
            Window = TimeSpan.FromMinutes(1),
            QueueLimit = 0,
            AutoReplenishment = true
        }));
    options.AddPolicy("telemetry-ticket", context => RateLimitPartition.GetFixedWindowLimiter(
        GetRateLimitPartition(context),
        _ => new FixedWindowRateLimiterOptions
        {
            PermitLimit = 30,
            Window = TimeSpan.FromMinutes(1),
            QueueLimit = 0,
            AutoReplenishment = true
        }));
});
builder.Services.Configure<AccountEmailOptions>(builder.Configuration.GetSection(AccountEmailOptions.SectionName));
builder.Services.AddSingleton<IAccountEmailSender, SmtpAccountEmailSender>();
builder.Services.AddSingleton<PasswordResetEmailQueue>();
builder.Services.AddSingleton<IPasswordResetEmailQueue>(services =>
    services.GetRequiredService<PasswordResetEmailQueue>());
builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddOptions<AuthOptions>()
    .Bind(builder.Configuration.GetSection(AuthOptions.SectionName))
    .Validate(x => x.AccessTokenMinutes > 0, "Auth:AccessTokenMinutes deve ser maior que zero.")
    .Validate(x => x.RefreshTokenDays > 0, "Auth:RefreshTokenDays deve ser maior que zero.")
    .Validate(x => x.RefreshReuseGraceSeconds >= 0, "Auth:RefreshReuseGraceSeconds não pode ser negativo.")
    .ValidateOnStart();
builder.Services.AddScoped<AuthTokenService>();
builder.Services.AddScoped<RefreshSessionService>();
builder.Services.AddScoped<AuthCookieService>();

// Configure PostgreSQL in production and allow SQLite only in Development.
var connectionString = builder.Configuration.GetConnectionString("DefaultConnection");
var explicitlyRequestedSqlite = builder.Configuration.GetValue<bool>("UseSqlite");
var hasSqliteConnection = connectionString?.Contains("Data Source", StringComparison.OrdinalIgnoreCase) == true ||
                          connectionString?.Contains(".db", StringComparison.OrdinalIgnoreCase) == true ||
                          connectionString?.Contains("sqlite", StringComparison.OrdinalIgnoreCase) == true;

if (!builder.Environment.IsDevelopment())
    ProductionDatabaseConfiguration.Validate(connectionString, explicitlyRequestedSqlite, hasSqliteConnection);

var useSqlite = DevelopmentDatabaseConfiguration.ShouldUseSqlite(
    builder.Environment.IsDevelopment(),
    explicitlyRequestedSqlite,
    connectionString);
var maximumDatabasePoolSize = builder.Configuration.GetValue<int?>("Database:MaximumPoolSize");
var boundedNpgsqlDataSource = !useSqlite && maximumDatabasePoolSize.HasValue
    ? NpgsqlDataSource.Create(DatabasePoolConfiguration.ApplyMaximumPoolSize(connectionString, maximumDatabasePoolSize.Value))
    : null;

builder.Services.AddDbContext<AppDbContext>(options =>
{
    if (useSqlite)
    {
        var sqliteConn = (connectionString != null && (connectionString.Contains("Data Source", StringComparison.OrdinalIgnoreCase) || connectionString.Contains(".db", StringComparison.OrdinalIgnoreCase)))
            ? connectionString
            : (builder.Configuration.GetConnectionString("SqliteConnection") ?? "Data Source=vending.db");
        options.UseSqlite(sqliteConn);
    }
    else if (boundedNpgsqlDataSource is not null)
    {
        options.UseNpgsql(boundedNpgsqlDataSource);
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
var jwtKey = builder.Configuration["Jwt:Key"];
if (string.IsNullOrWhiteSpace(jwtKey))
{
    if (!builder.Environment.IsDevelopment())
    {
        throw new InvalidOperationException("A configuração Jwt:Key é obrigatória fora do ambiente de desenvolvimento.");
    }

    jwtKey = "DevelopmentOnlyKeyForVendingMachines2026!";
}
if (Encoding.UTF8.GetByteCount(jwtKey) < 32)
{
    throw new InvalidOperationException("A configuração Jwt:Key deve ter pelo menos 32 bytes.");
}
if (!builder.Environment.IsDevelopment() && IsPlaceholderSecret(jwtKey))
{
    throw new InvalidOperationException("A configuração Jwt:Key não pode conter um valor de exemplo.");
}
builder.Configuration["Jwt:Key"] = jwtKey;

if (!builder.Environment.IsDevelopment())
{
    foreach (var requiredSecret in new[] { "MercadoPago:ClientSecret", "MercadoPago:WebhookSecret" })
    {
        if (IsPlaceholderSecret(builder.Configuration[requiredSecret]))
        {
            throw new InvalidOperationException($"A configuração {requiredSecret} é obrigatória fora do ambiente de desenvolvimento.");
        }
    }

    MercadoPagoEnvironmentConfiguration.Validate(
        builder.Configuration["MercadoPago:ClientId"],
        builder.Configuration["MercadoPago:WebhookUrl"]);
}

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

    c.AddSecurityRequirement(document => new OpenApiSecurityRequirement
    {
        [new OpenApiSecuritySchemeReference("Bearer", document)] = []
    });
});

// Configure CORS
var allowedCorsOrigins = builder.Configuration.GetSection("Cors:AllowedOrigins").Get<string[]>() ?? Array.Empty<string>();
if (builder.Environment.IsDevelopment())
{
    allowedCorsOrigins = allowedCorsOrigins
        .Concat(new[] { "http://localhost:5173", "http://localhost:8080" })
        .Distinct(StringComparer.OrdinalIgnoreCase)
        .ToArray();
}

var requireHttpsBrowserOrigins = !builder.Environment.IsDevelopment();
allowedCorsOrigins = CorsOriginConfiguration.Validate(allowedCorsOrigins, requireHttpsBrowserOrigins);

builder.Services.AddCors(options =>
{
    options.AddDefaultPolicy(policy =>
    {
        policy.WithOrigins(allowedCorsOrigins)
              .AllowCredentials()
              .AllowAnyMethod()
              .AllowAnyHeader()
              .WithExposedHeaders("X-Total-Count", "X-Page", "X-Page-Size", "X-Total-Pages");
    });
});

var configuredWebSocketOrigins = builder.Configuration.GetSection("WebSockets:AllowedOrigins").Get<string[]>();
if (configuredWebSocketOrigins is { Length: > 0 })
{
    configuredWebSocketOrigins = CorsOriginConfiguration.Validate(
        configuredWebSocketOrigins,
        requireHttpsBrowserOrigins);
}
var webSocketOriginPolicy = new WebSocketOriginPolicy(
    WebSocketOriginPolicy.ResolveOrigins(configuredWebSocketOrigins, allowedCorsOrigins));
builder.Services.AddSingleton(webSocketOriginPolicy);

// Telemetria: conexões físicas em memória, estado durável no PostgreSQL/SQLite.
builder.Services.AddSingleton<WebSocketTicketService>();
if (!useSqlite)
{
    if (boundedNpgsqlDataSource is not null)
        builder.Services.AddSingleton(boundedNpgsqlDataSource);
    else
        builder.Services.AddSingleton(_ => NpgsqlDataSource.Create(connectionString!));
    builder.Services.AddSingleton<PostgresTelemetryPanelEventBus>();
}
builder.Services.AddSingleton<TelemetryPanelHub>();
builder.Services.AddSingleton(_ => new BackgroundWorkerHealthRegistry(includeTelemetryPanelEventRelay: !useSqlite));
builder.Services.AddSingleton<ShutdownWriteRequestGate>();
builder.Services.AddScoped<DeploymentReadinessService>();
builder.Services.AddTelemetryConnectionOperations();
builder.Services.AddScoped<SessionOrchestrator>();
builder.Services.AddScoped<IDeviceTelemetryMessageHandler>(sp => sp.GetRequiredService<SessionOrchestrator>());
builder.Services.AddSingleton<DeviceMessageInboxAdmissionGate>();
builder.Services.AddScoped<DeviceMessageInboxService>();
builder.Services.AddSingleton<WebhookInboxAdmissionGate>(_ => WebhookInboxAdmissionGate.Shared);
builder.Services.AddScoped<WebhookInboxAdmissionService>();
builder.Services.AddScoped<DeviceMessageInboxProcessor>();
builder.Services.AddHttpClient("MercadoPago", client => client.Timeout = TimeSpan.FromSeconds(20));
builder.Services.AddHttpClient("TelemetryLegacyOwner", client => client.Timeout = TimeSpan.FromSeconds(20))
    .ConfigurePrimaryHttpMessageHandler(() => new HttpClientHandler { AllowAutoRedirect = false });
builder.Services.AddSingleton<LegacyTelemetryRequestRelay>();
builder.Services.AddScoped<MercadoPagoWebhookProcessor>();
builder.Services.AddScoped<OperationalAlertSyncService>();
builder.Services.Configure<MdbStatusOptions>(builder.Configuration.GetSection("MdbStatus"));
builder.Services.AddScoped<MdbStatusRequestService>();
builder.Services.AddDeploymentBackgroundWorkers(passiveDeploymentMode, includePostgresTelemetryPanelRelay: !useSqlite);

var app = builder.Build();

// Passive candidate instances must not initialize or otherwise touch the shared database at startup.
if (!passiveDeploymentMode)
{
    using var scope = app.Services.CreateScope();
    var services = scope.ServiceProvider;
    var logger = services.GetRequiredService<ILogger<Program>>();
    try
    {
        var context = services.GetRequiredService<AppDbContext>();
        DbInitializer.Initialize(context, app.Environment.IsDevelopment());
        if (builder.Configuration.GetValue<bool>("Telemetry:RequireDeviceCredentials") &&
            DeviceCredentialReadiness.HasActiveMachinesWithoutValidCredential(context.Machines))
        {
            throw new InvalidOperationException(
                "Telemetry:RequireDeviceCredentials exige credenciais válidas provisionadas para todas as máquinas ativas.");
        }
        await LegacyOwnerStartupGuard.EnsureReadyBeforeWorkersAsync(
            context,
            services.GetRequiredService<LegacyTelemetryRequestRelay>(),
            CancellationToken.None);
        // Não limpe o estado de toda a frota no startup. Durante uma troca gradual,
        // outra instância ainda pode manter WebSockets ativos; a limpeza global faria
        // essas máquinas parecerem indisponíveis até o próximo heartbeat. Cada conexão
        // atualiza o próprio estado e a leitura de status expira LastSeenAt naturalmente.
    }
    catch (Exception ex)
    {
        logger.LogCritical(ex, "Falha crítica ao inicializar o banco de dados. A aplicação será encerrada.");
        throw; // Impede o app de subir com banco desatualizado
    }
}
else
{
    app.Logger.LogWarning("API iniciada em modo passivo: workers, inicialização do banco e rotas de aplicação permanecem desativados.");
}

// Configure the HTTP request pipeline.
// Só confia em cabeçalhos encaminhados quando os IPs do proxy foram configurados explicitamente.
if (forwardedHeadersOptions is not null)
{
    app.UseForwardedHeaders(forwardedHeadersOptions);
}

app.UseMiddleware<VendingMachines.Api.Middlewares.PassiveDeploymentModeMiddleware>(passiveDeploymentMode);

if (app.Environment.IsDevelopment())
{
    app.UseSwagger();
    app.UseSwaggerUI();
}

app.UseMiddleware<VendingMachines.Api.Middlewares.SecurityHeadersMiddleware>();
app.UseRouting();
app.UseMiddleware<VendingMachines.Api.Middlewares.LegacyRelayLoopGuardMiddleware>();
app.UseCors();
app.UseRateLimiter();
app.UseAuthentication();
app.UseMiddleware<VendingMachines.Api.Middlewares.UserRoleValidationMiddleware>();
app.UseAuthorization();
app.UseMiddleware<VendingMachines.Api.Middlewares.ShutdownWriteRequestMiddleware>();
app.UseMiddleware<VendingMachines.Api.Middlewares.ImpersonationMiddleware>();
var webSocketOptions = new WebSocketOptions
{
    KeepAliveInterval = TimeSpan.FromSeconds(30),
    KeepAliveTimeout = TimeSpan.FromSeconds(30)
};
app.UseWebSockets(webSocketOptions);
app.UseMiddleware<VendingMachines.Api.Middlewares.WebSocketMiddleware>();

app.MapControllers();

// Health check endpoints para monitoramento do Coolify e testes
app.MapGet("/health", () => Results.Ok(new { status = "Healthy", timestamp = DateTime.UtcNow }));
app.MapGet("/health/ready", ApiReadinessEndpoint.HandleAsync);
app.MapGet("/health/deploy-ready", DeploymentReadinessEndpoint.HandleAsync);
app.MapGet("/", () => Results.Ok(new { app = "Vending Machines API", status = "Online" }));

app.Run();

static string GetRateLimitPartition(HttpContext context) =>
    context.Connection.RemoteIpAddress?.ToString() ?? "unknown-client";

static bool IsPlaceholderSecret(string? value) =>
    string.IsNullOrWhiteSpace(value) ||
    new[] { "change_me", "replace_with", "your_", "placeholder", "example", "dummy", "cole_aqui" }
        .Any(marker => value.Contains(marker, StringComparison.OrdinalIgnoreCase));

static bool IsDevelopmentEnvironment(string? value) =>
    string.Equals(value?.Trim(), "Development", StringComparison.OrdinalIgnoreCase);
