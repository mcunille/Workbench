// Copyright (c) 2026 The White Stag Collection.

using Microsoft.AspNetCore.Diagnostics.HealthChecks;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Workbench.Server.Application;
using Workbench.Server.Contracts;
using Workbench.Server.Health;
using Workbench.Server.Http;

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
builder.Services.AddExceptionHandler<GlobalExceptionHandler>();
builder.Services.AddHealthChecks().AddCheck(
    "self",
    () => HealthCheckResult.Healthy(),
    tags: ["live"]);
builder.Services.AddSingleton<IReleaseInformation, AssemblyReleaseInformation>();
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

app.MapGet(
        "/api/system",
        (IReleaseInformation releaseInformation) =>
            new SystemResponse("Workbench", releaseInformation.Version))
    .WithName("GetSystem")
    .Produces<SystemResponse>();

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

public partial class Program;
