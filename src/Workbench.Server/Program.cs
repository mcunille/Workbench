using Workbench.Server.Application;
using Workbench.Server.Contracts;
using Workbench.Server.Http;

var builder = WebApplication.CreateBuilder(args);
builder.Services.AddExceptionHandler<GlobalExceptionHandler>();
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

app.Map("/api/{**path}", () => Results.Problem(
    statusCode: StatusCodes.Status404NotFound,
    title: "API route not found.",
    type: "https://www.rfc-editor.org/rfc/rfc9110#section-15.5.5"));

app.MapFallbackToFile("index.html");

app.Run();

public partial class Program;
