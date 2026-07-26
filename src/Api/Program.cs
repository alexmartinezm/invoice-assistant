using System.Security.Claims;
using System.Text;
using System.Threading.RateLimiting;
using Api.Assistant;
using Api.Domain;
using Api.Features.Auth;
using Api.Features.Config;
using Api.Features.Customers;
using Api.Features.Invoices;
using Api.Features.Reports;
using Api.Infrastructure;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Authorization;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Tokens;

var builder = WebApplication.CreateBuilder(args);

builder.Configuration
    .AddDotEnvFile(builder.Environment.ContentRootPath)
    .AddFlatVariableAliases();

// --- Persistence ------------------------------------------------------------------------------
builder.Services.AddSingleton<IClock, SystemClock>();
builder.Services.AddDbContext<AppDbContext>(options => options.UseNpgsql(
    builder.Configuration.GetConnectionString("Postgres")
    ?? "Host=localhost;Port=5432;Database=invoice_assistant;Username=postgres;Password=postgres"));

// --- Business ---------------------------------------------------------------------------------
builder.Services.Configure<InvoicingOptions>(builder.Configuration.GetSection(InvoicingOptions.SectionName));

// --- Auth -------------------------------------------------------------------------------------
builder.Services.Configure<JwtOptions>(builder.Configuration.GetSection(JwtOptions.SectionName));
builder.Services.AddScoped<TokenIssuer>();
builder.Services
    .AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
    .AddJwtBearer(options =>
    {
        var jwt = builder.Configuration.GetSection(JwtOptions.SectionName).Get<JwtOptions>() ?? new JwtOptions();

        options.TokenValidationParameters = new TokenValidationParameters
        {
            ValidateIssuer = true,
            ValidIssuer = jwt.Issuer,
            ValidateAudience = true,
            ValidAudience = jwt.Audience,
            ValidateIssuerSigningKey = true,
            IssuerSigningKey = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(jwt.SigningKey)),
            ValidateLifetime = true,
            ClockSkew = TimeSpan.FromSeconds(30),
            RoleClaimType = ClaimTypes.Role,
            NameClaimType = ClaimTypes.NameIdentifier,
        };
    });

builder.Services.AddAuthorizationBuilder()
    .SetDefaultPolicy(new AuthorizationPolicyBuilder().RequireAuthenticatedUser().Build())
    .AddPolicy(Policies.Accountant, policy => policy.RequireRole(nameof(Role.Accountant), nameof(Role.Admin)))
    .AddPolicy(Policies.Admin, policy => policy.RequireRole(nameof(Role.Admin)));

// --- Assistant --------------------------------------------------------------------------------
builder.Services.AddHttpContextAccessor();
builder.Services.AddAssistant(builder.Configuration);

// Every turn costs money, so the chat endpoint is limited per user rather than per IP.
builder.Services.AddRateLimiter(limiter =>
{
    limiter.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
    limiter.AddPolicy(ChatEndpoints.RateLimitPolicy, context =>
    {
        var turnsPerMinute = context.RequestServices
            .GetRequiredService<IOptions<AssistantOptions>>().Value.TurnsPerMinute;

        var partition = context.User.Identity?.IsAuthenticated is true
            ? context.User.Id().ToString()
            : context.Connection.RemoteIpAddress?.ToString() ?? "anonymous";

        return RateLimitPartition.GetFixedWindowLimiter(partition, _ => new FixedWindowRateLimiterOptions
        {
            PermitLimit = turnsPerMinute,
            Window = TimeSpan.FromMinutes(1),
        });
    });
});

// --- Cross-cutting ----------------------------------------------------------------------------
builder.Services.AddProblemDetails();
builder.Services.AddExceptionHandler<DomainExceptionHandler>();
builder.Services.AddAppTelemetry(builder.Configuration, builder.Environment);

var app = builder.Build();

await app.MigrateAndSeedAsync();

app.UseExceptionHandler();

// No CORS configuration anywhere: the Vite dev server proxies /api, and in production the API
// serves the built SPA itself. The browser only ever talks to one origin.
app.UseAuthentication();
app.UseAuthorization();
app.UseRateLimiter();

app.MapGet("/health", () => Results.Ok(new { status = "ok" })).AllowAnonymous();

app.MapConfigEndpoints()
    .MapAuthEndpoints()
    .MapCustomerEndpoints()
    .MapInvoiceEndpoints()
    .MapReportEndpoints()
    .MapChatEndpoints();

// Production single-container layout: the built SPA lands in wwwroot and the API serves it.
if (Directory.Exists(Path.Combine(app.Environment.WebRootPath ?? "wwwroot", "assets")))
{
    app.UseDefaultFiles();
    app.UseStaticFiles();
    app.MapFallbackToFile("index.html");
}

app.Run();

/// <summary>Exposed so the integration tests can boot the real application.</summary>
public partial class Program;
