using VendingMachines.Api.Services;

var builder = WebApplication.CreateBuilder(args);

// Add services to the container.
builder.Services.AddControllers();

// Register Mock Data Service
builder.Services.AddSingleton<IMockDataService, MockDataService>();

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
