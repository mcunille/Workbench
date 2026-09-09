// Copyright (c) 2026 The White Stag Collection.

using System.Data;
using System.Security.Claims;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Workbench.Server.Http;
using Workbench.Server.Identity;
using Workbench.Server.Persistence;

namespace Workbench.Server.Inventory;

public sealed class ItemExportCapacity : IDisposable
{
    private readonly SemaphoreSlim _slots = new(2, 2);
    public bool TryEnter() => _slots.Wait(0);
    public void Release() => _slots.Release();
    public void Dispose() => _slots.Dispose();
}

public static class ItemExportEndpoints
{
    public static RouteGroupBuilder MapItemExport(this RouteGroupBuilder group)
    {
        group.MapPost("/export", ExportAsync)
            .WithMetadata(WorkbenchAntiforgeryMetadata.Instance)
            .Produces(StatusCodes.Status200OK, contentType: "text/csv")
            .Produces(StatusCodes.Status204NoContent)
            .ProducesValidationProblem()
            .ProducesProblem(StatusCodes.Status401Unauthorized)
            .ProducesProblem(StatusCodes.Status422UnprocessableEntity)
            .ProducesProblem(StatusCodes.Status429TooManyRequests)
            .ProducesProblem(StatusCodes.Status503ServiceUnavailable);
        return group.MapItemPackage();
    }

    private static async Task<IResult> ExportAsync(ExportItemsRequest request, HttpContext http,
        WorkbenchDbContext database, ItemExportCapacity capacity, SessionService sessions,
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
        deadline.CancelAfter(TimeSpan.FromSeconds(30));
        var cancellationToken = deadline.Token;
        try
        {
            List<ExportItem> items;
            DateTimeOffset exportedAt;
            // Serializable range locks cover the scope predicate and ordering until the whole projection is read.
            await using (var transaction = await database.Database.BeginTransactionAsync(IsolationLevel.Serializable, cancellationToken))
            {
                items = await database.Items.AsNoTracking().TagWith("H7 collection export")
                    .Where(item => request.Scope == "all" || item.ArchivedAtUtc == null)
                    .OrderBy(item => item.CreatedAtUtc).ThenBy(item => item.Id)
                    .Select(item => new ExportItem(item.Id, item.TrackingKind, item.Name, item.Notes,
                        item.StorageLocation, item.CreatedAtUtc, item.ArchivedAtUtc))
                    .Take(ItemExportCsv.MaximumRows + 1).ToListAsync(cancellationToken);
                if (items.Count > ItemExportCsv.MaximumRows)
                    throw new ItemExportLimitException();
                exportedAt = timeProvider.GetUtcNow();
                await transaction.CommitAsync(cancellationToken);
            }
            var bytes = items.Count == 0 ? null : ItemExportCsv.Encode(items, request.Scope, exportedAt, cancellationToken);
            if (!await SessionStillValidAsync(http, database, sessions, cookieOptions, timeProvider, cancellationToken))
                return Results.Unauthorized();
            cancellationToken.ThrowIfCancellationRequested();
            if (bytes is null)
                return Results.NoContent();
            return Results.File(bytes, "text/csv; charset=utf-8",
                $"workbench-records-v1-{request.Scope}-{exportedAt.UtcDateTime:yyyyMMdd'T'HHmmss'Z'}.csv");
        }
        catch (ItemExportLimitException)
        {
            return Failure(422, "export_limit_exceeded", "Export supports up to 10,000 records and 32 MiB. If archived records were included, try Active records.");
        }
        catch (OperationCanceledException) when (!http.RequestAborted.IsCancellationRequested)
        {
            return PreparationFailed();
        }
        catch (SqlException)
        {
            return PreparationFailed();
        }
        finally
        {
            capacity.Release();
        }
    }

    internal static async Task<bool> SessionStillValidAsync(HttpContext http, WorkbenchDbContext database,
        SessionService sessions, IOptionsMonitor<CookieAuthenticationOptions> cookieOptions,
        TimeProvider timeProvider, CancellationToken cancellationToken)
    {
        // Re-open the protected cookie and resolve authoritatively, rather than accepting cached authentication.
        var options = cookieOptions.Get(SessionCookieHandler.Scheme);
        var cookie = options.CookieManager.GetRequestCookie(http, options.Cookie.Name!);
        var token = cookie is null ? null : options.TicketDataFormat.Unprotect(cookie)?.Principal
            .FindFirst(SessionCookieHandler.SessionTokenClaimType)?.Value;
        var session = token is null ? null : await sessions.ResolveAsync(token, timeProvider.GetUtcNow(), cancellationToken);
        return session is not null && session.TenantId == database.TenantContext.RequireTenantId() &&
            session.UserId.ToString("N") == http.User.FindFirstValue(ClaimTypes.NameIdentifier) &&
            session.SessionId.ToString("N") == http.User.FindFirstValue(SessionCookieHandler.SessionIdClaimType);
    }

    private static IResult PreparationFailed() => Failure(503, "export_preparation_failed", "Export preparation failed. Retry to prepare a new snapshot.");
    private static IResult Failure(int status, string code, string title) => Results.Problem(statusCode: status, title: title,
        extensions: new Dictionary<string, object?> { ["code"] = code });
}
