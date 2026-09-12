// Copyright (c) 2026 The White Stag Collection.

using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Options;
using System.Text.Json.Serialization;
using Workbench.Server.Http;
using Workbench.Server.Identity;
using Workbench.Server.Persistence;
using Workbench.Server.Storage;

namespace Workbench.Server.Inventory;

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record ExportPackageRequest(string? Scope);

public static class ItemPackageEndpoints
{
    public static RouteGroupBuilder MapItemPackage(this RouteGroupBuilder group)
    {
        group.MapPost("/export-package", ExportAsync)
            .WithMetadata(WorkbenchAntiforgeryMetadata.Instance)
            .Produces(StatusCodes.Status200OK, contentType: "application/zip")
            .Produces(StatusCodes.Status204NoContent)
            .ProducesValidationProblem()
            .ProducesProblem(StatusCodes.Status401Unauthorized)
            .ProducesProblem(StatusCodes.Status422UnprocessableEntity)
            .ProducesProblem(StatusCodes.Status429TooManyRequests)
            .ProducesProblem(StatusCodes.Status503ServiceUnavailable);
        return group;
    }

    private static async Task<IResult> ExportAsync(ExportPackageRequest request, HttpContext http,
        WorkbenchDbContext database, IBlobStore store, ItemExportCapacity capacity, SessionService sessions,
        IOptionsMonitor<CookieAuthenticationOptions> cookieOptions, TimeProvider timeProvider)
    {
        if (request.Scope is not ("active" or "all"))
            return Results.ValidationProblem(new Dictionary<string, string[]> { ["scope"] = ["Choose active records or active and archived records."] });
        if (!capacity.TryEnter())
        {
            http.Response.Headers.RetryAfter = "5";
            return Failure(429, "export_busy", "Export preparation is busy. Retry shortly.");
        }
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(http.RequestAborted);
        deadline.CancelAfter(TimeSpan.FromSeconds(120));
        var cancellationToken = deadline.Token;
        try
        {
            var snapshot = await ItemPackageSnapshot.CaptureAsync(database, request.Scope, store.Alias, timeProvider, cancellationToken);
            var bytes = snapshot.Items.Count == 0 ? null : await ItemPackageArchive.EncodeAsync(snapshot.Items,
                database.TenantContext.RequireTenantId(), request.Scope, snapshot.ExportedAt, store, cancellationToken, snapshot.Documents);
            if (!await ItemExportEndpoints.SessionStillValidAsync(http, database, sessions, cookieOptions, timeProvider, cancellationToken))
                return Results.Unauthorized();
            cancellationToken.ThrowIfCancellationRequested();
            if (bytes is null) return Results.NoContent();
            return Results.File(bytes, "application/zip",
                $"workbench-package-v2-{request.Scope}-{snapshot.ExportedAt.UtcDateTime:yyyyMMdd'T'HHmmss'Z'}.zip");
        }
        catch (ItemExportLimitException)
        {
            return Failure(422, "export_limit_exceeded", "Package supports 10,000 records, 10,000 documents, 32 MiB CSV, 16 MiB manifest and 128 MiB content/ZIP. Try Active records if archived records were included, or CSV without photographs or document files.");
        }
        catch (OperationCanceledException) when (!http.RequestAborted.IsCancellationRequested)
        {
            return PreparationFailed();
        }
        catch (Exception exception) when (exception is SqlException or IOException or InvalidDataException or Azure.RequestFailedException or Azure.Identity.AuthenticationFailedException or UnauthorizedAccessException)
        {
            return PreparationFailed();
        }
        finally { capacity.Release(); }
    }

    private static IResult PreparationFailed() => Failure(503, "export_preparation_failed", "The complete package could not be prepared. Retry a new snapshot. If photograph or document failures persist, ask the operator to investigate storage recovery.");
    private static IResult Failure(int status, string code, string title) => Results.Problem(statusCode: status, title: title,
        extensions: new Dictionary<string, object?> { ["code"] = code });
}
