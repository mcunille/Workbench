using System.Reflection;
using Workbench.Server.Contracts;

var builder = WebApplication.CreateBuilder(args);
builder.Services.AddOpenApi();

var app = builder.Build();

app.MapGet(
        "/api/system",
        () =>
        {
            var version = typeof(Program).Assembly
                .GetCustomAttribute<AssemblyInformationalVersionAttribute>()
                ?.InformationalVersion;

            return new SystemResponse("Workbench", version ?? "0.0.0-local");
        })
    .WithName("GetSystem")
    .Produces<SystemResponse>();

app.Run();

public partial class Program;
