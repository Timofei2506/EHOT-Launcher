using System.Security.Claims;
using System.Text;
using System.Threading.RateLimiting;
using EHOT.Launcher.API.Data;
using EHOT.Launcher.API.Endpoints;
using EHOT.Launcher.API.Services;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.IdentityModel.Tokens;

var builder = WebApplication.CreateBuilder(args);

// ---------------------------------------------------------------------------
// Конфигурация. Секреты НЕ хранятся в репозитории: переменные окружения
// (EHOT_JWT_SECRET, EHOT_INVITE_SECRET) или user-secrets/dotnet secret store.
// ---------------------------------------------------------------------------
var options = new ApiOptions
{
    JwtSecret = builder.Configuration["Auth:JwtSecret"]
                ?? Environment.GetEnvironmentVariable("EHOT_JWT_SECRET")
                ?? string.Empty,
    InviteSecret = builder.Configuration["Auth:InviteSecret"]
                   ?? Environment.GetEnvironmentVariable("EHOT_INVITE_SECRET")
                   ?? string.Empty,
    MaxRoomsPerIdentity = builder.Configuration.GetValue("Rooms:MaxPerIdentity", 3)
};

if (!options.IsConfigured && !builder.Environment.IsDevelopment())
{
    throw new InvalidOperationException(
        "Не заданы секреты EHOT_JWT_SECRET / EHOT_INVITE_SECRET (минимум 32 символа). " +
        "Секреты нельзя хранить в исходном коде или в appsettings.json.");
}

var connectionString = builder.Configuration.GetConnectionString("Postgres")
                       ?? Environment.GetEnvironmentVariable("EHOT_POSTGRES")
                       ?? "Host=localhost;Database=ehot;Username=ehot;Password=ehot";

builder.Services.AddSingleton(options);
builder.Services.AddSingleton<TokenService>();
builder.Services.AddSingleton<InviteService>();

builder.Services.AddDbContext<ControlPlaneDbContext>(o =>
{
    if (builder.Environment.IsEnvironment("Testing"))
    {
        o.UseSqlite(connectionString);
    }
    else
    {
        o.UseNpgsql(connectionString, npgsql => npgsql.EnableRetryOnFailure(3));
    }
});

builder.Services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme).AddJwtBearer(o =>
{
    o.TokenValidationParameters = new TokenValidationParameters
    {
        ValidateIssuer = true,
        ValidIssuer = options.Issuer,
        ValidateAudience = true,
        ValidAudience = options.Audience,
        ValidateIssuerSigningKey = true,
        IssuerSigningKey = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(
            options.JwtSecret.Length >= 32 ? options.JwtSecret : new string('d', 32))),
        ValidateLifetime = true,
        ClockSkew = TimeSpan.FromMinutes(2),
        NameClaimType = "name"
    };
});

builder.Services.AddAuthorization();

// Ограничение частоты запросов: защита от злоупотреблений и DoS.
builder.Services.AddRateLimiter(o =>
{
    o.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
    o.GlobalLimiter = PartitionedRateLimiter.Create<HttpContext, string>(context =>
    {
        var key = context.User.FindFirstValue(ClaimTypes.NameIdentifier)
                  ?? context.User.FindFirstValue("sub")
                  ?? context.Connection.RemoteIpAddress?.ToString()
                  ?? "anonymous";

        return RateLimitPartition.GetFixedWindowLimiter(key, _ => new FixedWindowRateLimiterOptions
        {
            PermitLimit = 120,
            Window = TimeSpan.FromMinutes(1),
            QueueLimit = 10,
            QueueProcessingOrder = QueueProcessingOrder.OldestFirst
        });
    });

    o.AddPolicy("room-create", context => RateLimitPartition.GetFixedWindowLimiter(
        context.User.FindFirstValue("sub") ?? "anonymous",
        _ => new FixedWindowRateLimiterOptions
        {
            PermitLimit = 10,
            Window = TimeSpan.FromMinutes(10)
        }));
});

builder.Services.AddHealthChecks().AddDbContextCheck<ControlPlaneDbContext>();
builder.Services.AddProblemDetails();
builder.Services.AddOpenApi();
builder.Services.AddResponseCompression();
builder.Services.Configure<ForwardedHeadersOptions>(o =>
    o.ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto);

var app = builder.Build();

app.UseForwardedHeaders();
app.UseExceptionHandler();
app.UseResponseCompression();
app.UseRateLimiter();

if (app.Environment.IsDevelopment())
{
    app.MapOpenApi();
}

app.UseAuthentication();
app.UseAuthorization();

app.MapHealthChecks("/healthz");
app.MapIdentityEndpoints();
app.MapRoomEndpoints();
app.MapRelayEndpoints();

// Создание схемы: в production — только через миграции (dotnet ef database update),
// в Testing/Development допускаем EnsureCreated для локального запуска.
using (var scope = app.Services.CreateScope())
{
    var db = scope.ServiceProvider.GetRequiredService<ControlPlaneDbContext>();
    if (app.Environment.IsDevelopment() || app.Environment.IsEnvironment("Testing"))
    {
        await db.Database.EnsureCreatedAsync();
    }
}

app.Run();

/// <summary>Точка входа для интеграционных тестов (WebApplicationFactory).</summary>
public partial class Program;
