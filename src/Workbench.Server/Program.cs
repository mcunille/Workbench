// Copyright (c) 2026 The White Stag Collection.

using Microsoft.AspNetCore.Diagnostics.HealthChecks;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using System.Security.Claims;
using System.Security.Cryptography.X509Certificates;
using Workbench.Server.Application;
using Workbench.Server.Administration;
using Workbench.Server.Authorization;
using Workbench.Server.Contracts;
using Workbench.Server.Health;
using Workbench.Server.Http;
using Workbench.Server.Identity;
using Workbench.Server.Persistence;
using Workbench.Server.Security;
using Workbench.Server.Tenancy;
using DurableSessionOptions = Workbench.Server.Identity.SessionOptions;

if (args is ["--health-check"])
{
    var configuredUrl = Environment.GetEnvironmentVariable("WORKBENCH_HEALTH_URL")
        ?? "http://127.0.0.1:8080/health/ready";

    if (!Uri.TryCreate(configuredUrl, UriKind.Absolute, out var readinessUri))
    {
        Environment.ExitCode = 1;
        return;
    }

    using var healthClient = new HttpClient { Timeout = TimeSpan.FromSeconds(5) };
    Environment.ExitCode = await HealthProbe.RunAsync(healthClient, readinessUri);
    return;
}

var builder = WebApplication.CreateBuilder(args);
var configuredWebConnection =
    ProductionSecurityConfigurationValidator.GetWebConnectionString(builder.Configuration);
builder.Services.AddExceptionHandler<GlobalExceptionHandler>();
builder.Services.AddHealthChecks().AddCheck(
    "self",
    () => HealthCheckResult.Healthy(),
    tags: ["live"]);
builder.Services.AddSingleton<IReleaseInformation, AssemblyReleaseInformation>();
builder.Services.AddHostedService<ProductionSecurityConfigurationValidator>();
builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddSingleton(new DurableSessionOptions());
builder.Services.AddSingleton<SessionService>(services => new SessionService(
    RequireWebConnectionString(services.GetRequiredService<IConfiguration>()),
    services.GetRequiredService<DurableSessionOptions>()));
builder.Services.AddScoped<IIdentityVerifier>(services => new BuiltInPasswordVerifier(
    RequireWebConnectionString(services.GetRequiredService<IConfiguration>()),
    services.GetRequiredService<IPasswordHasher<WorkbenchUser>>()));
if (builder.Environment.IsDevelopment())
{
    builder.Services.AddSingleton<DevelopmentIdentityMessageDelivery>();
    builder.Services.AddSingleton<IIdentityMessageDelivery>(services =>
        services.GetRequiredService<DevelopmentIdentityMessageDelivery>());
    builder.Services.AddSingleton<ISensitiveRequestRateLimiter, DevelopmentSensitiveRequestRateLimiter>();
}
else
{
    builder.Services.AddSingleton<IIdentityMessageDelivery, DisabledIdentityMessageDelivery>();
    builder.Services.AddSingleton<ISensitiveRequestRateLimiter, DisabledSensitiveRequestRateLimiter>();
}
builder.Services.AddScoped<IdentityOperationService>(services => new IdentityOperationService(
    RequireWebConnectionString(services.GetRequiredService<IConfiguration>()),
    services.GetRequiredService<IIdentityMessageDelivery>(),
    services.GetRequiredService<ISensitiveRequestRateLimiter>(),
    services.GetRequiredService<UserManager<WorkbenchUser>>(),
    services.GetRequiredService<TimeProvider>()));
builder.Services.AddScoped<SecurityAuditWriter>();
builder.Services
    .AddIdentityCore<WorkbenchUser>(options =>
    {
        options.Password.RequiredLength = 14;
        options.Password.RequiredUniqueChars = 4;
        options.Password.RequireDigit = true;
        options.Password.RequireLowercase = true;
        options.Password.RequireUppercase = true;
        options.Password.RequireNonAlphanumeric = true;
    })
    .AddRoles<WorkbenchRole>()
    .AddEntityFrameworkStores<WorkbenchDbContext>();
builder.Services.AddScoped<SessionAuthenticationEvents>();
builder.Services.AddHttpContextAccessor();
builder.Services.AddScoped(services =>
{
    var principal = services.GetRequiredService<IHttpContextAccessor>().HttpContext?.User;
    return new TenantContext(ParseGuidClaim(principal, SessionCookieHandler.TenantIdClaimType));
});
builder.Services.AddScoped(services =>
{
    var principal = services.GetRequiredService<IHttpContextAccessor>().HttpContext?.User;
    return new RequestActor(
        ParseRequiredGuidClaim(principal, ClaimTypes.NameIdentifier),
        ParseRequiredGuidClaim(principal, SessionCookieHandler.TenantIdClaimType),
        ParseRequiredGuidClaim(principal, SessionCookieHandler.SessionIdClaimType),
        principal?.FindAll(SessionCookieHandler.PermissionClaimType)
            .Select(claim => claim.Value)
            .ToHashSet(StringComparer.Ordinal) ?? []);
});
builder.Services.AddDbContext<WorkbenchDbContext>((services, options) =>
{
    options.UseSqlServer(RequireWebConnectionString(services.GetRequiredService<IConfiguration>()));
    options.AddInterceptors(
        new TenantConnectionInterceptor(services.GetRequiredService<TenantContext>()),
        new TenantSaveChangesInterceptor(services.GetRequiredService<TenantContext>()));
});

var dataProtection = builder.Services
    .AddDataProtection()
    .SetApplicationName("Workbench");
if (!string.IsNullOrWhiteSpace(configuredWebConnection))
{
    dataProtection.PersistKeysToDbContext<WorkbenchDbContext>();
}
if (builder.Environment.IsProduction())
{
    var certificatePath = ProductionSecurityConfigurationValidator.GetCertificatePath(builder.Configuration);
    if (!string.IsNullOrWhiteSpace(certificatePath))
    {
        var certificatePassword = builder.Configuration["DataProtection:CertificatePassword"]
            ?? Environment.GetEnvironmentVariable("WORKBENCH_DATA_PROTECTION_CERTIFICATE_PASSWORD");
        var certificate = X509CertificateLoader.LoadPkcs12FromFile(
            certificatePath,
            certificatePassword,
            X509KeyStorageFlags.EphemeralKeySet);
        dataProtection.ProtectKeysWithCertificate(certificate);
    }
}

builder.Services
    .AddAuthentication(SessionCookieHandler.Scheme)
    .AddCookie(SessionCookieHandler.Scheme, options =>
    {
        options.Cookie.Name = builder.Environment.IsDevelopment()
            ? ".Workbench.Session"
            : "__Host-Workbench.Session";
        options.Cookie.HttpOnly = true;
        options.Cookie.SameSite = SameSiteMode.Lax;
        options.Cookie.SecurePolicy = builder.Environment.IsDevelopment()
            ? CookieSecurePolicy.SameAsRequest
            : CookieSecurePolicy.Always;
        options.Cookie.Path = "/";
        options.ExpireTimeSpan = TimeSpan.FromHours(12);
        options.SlidingExpiration = false;
        options.EventsType = typeof(SessionAuthenticationEvents);
        options.LoginPath = PathString.Empty;
        options.AccessDeniedPath = PathString.Empty;
    });
builder.Services.AddAuthorization(options =>
{
    options.AddPolicy(
        WorkbenchPermissions.TenantUsersManage,
        policy => policy.RequireClaim(
            SessionCookieHandler.PermissionClaimType,
            WorkbenchPermissions.TenantUsersManage));
});
builder.Services.AddAntiforgery(options =>
{
    options.HeaderName = "X-CSRF-TOKEN";
    options.Cookie.Name = builder.Environment.IsDevelopment()
        ? ".Workbench.Antiforgery"
        : "__Host-Workbench.Antiforgery";
    options.Cookie.HttpOnly = true;
    options.Cookie.SameSite = SameSiteMode.Strict;
    options.Cookie.SecurePolicy = builder.Environment.IsDevelopment()
        ? CookieSecurePolicy.SameAsRequest
        : CookieSecurePolicy.Always;
    options.Cookie.Path = "/";
});
builder.Services.AddOpenApi();
builder.Services.AddProblemDetails(options =>
{
    options.CustomizeProblemDetails = context =>
        context.ProblemDetails.Extensions["traceId"] = context.HttpContext.TraceIdentifier;
});

var app = builder.Build();

app.UseExceptionHandler();
app.UseDefaultFiles();
app.UseStaticFiles();
app.UseRouting();
app.UseAuthentication();
app.UseAuthorization();
app.UseMiddleware<WorkbenchAntiforgeryMiddleware>();

app.MapGet(
        "/api/system",
        (IReleaseInformation releaseInformation) =>
            new SystemResponse("Workbench", releaseInformation.Version))
    .WithName("GetSystem")
    .Produces<SystemResponse>();

app.MapWorkbenchAuthentication();
app.MapWorkbenchRecovery();
app.MapTenantUserAdministration();

app.MapHealthChecks("/health/live", new HealthCheckOptions
{
    Predicate = registration => registration.Tags.Contains("live"),
    ResponseWriter = HealthResponseWriter.WriteAsync,
});

app.MapHealthChecks("/health/ready", new HealthCheckOptions
{
    Predicate = registration =>
        registration.Tags.Contains("live") || registration.Tags.Contains("ready"),
    ResponseWriter = HealthResponseWriter.WriteAsync,
});

app.Map("/api/{**path}", () => Results.Problem(
    statusCode: StatusCodes.Status404NotFound,
    title: "API route not found.",
    type: "https://www.rfc-editor.org/rfc/rfc9110#section-15.5.5"));

app.MapFallbackToFile("index.html");

app.Run();

static string RequireWebConnectionString(IConfiguration configuration) =>
    ProductionSecurityConfigurationValidator.GetWebConnectionString(configuration)
    ?? throw new InvalidOperationException("The Workbench web database connection is not configured.");

static Guid? ParseGuidClaim(ClaimsPrincipal? principal, string claimType) =>
    Guid.TryParse(principal?.FindFirst(claimType)?.Value, out var value) ? value : null;

static Guid ParseRequiredGuidClaim(ClaimsPrincipal? principal, string claimType) =>
    ParseGuidClaim(principal, claimType)
    ?? throw new InvalidOperationException("An authoritative request actor is required.");

public partial class Program;
