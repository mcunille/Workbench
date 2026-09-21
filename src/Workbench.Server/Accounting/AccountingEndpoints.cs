// Copyright (c) 2026 The White Stag Collection.
using System.Data;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Workbench.Server.Authorization;
using Workbench.Server.Http;
using Workbench.Server.Persistence;

namespace Workbench.Server.Accounting;

public static class AccountingEndpoints
{
    internal static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    public static void MapAccounting(this IEndpointRouteBuilder endpoints)
    {
        var group = endpoints.MapGroup("/api/beta/accounting").WithTags("Accounting").RequireAuthorization("AccountingConfigurationRead");
        group.AddEndpointFilter(async (context, next) => { context.HttpContext.Response.Headers.CacheControl = "private, no-store"; return await next(context); });
        group.MapGet("/catalog", () => Results.Ok(AccountingCatalog.Value)).Produces<AccountingCatalogResponse>();
        group.MapGet("/setup", ReadSetup).Produces<AccountingSetupResponse>();
        group.MapGet("/accounts", Browse).Produces<AccountingAccountPage>().ProducesProblem(400);
        Write(group.MapPut("/setup", SaveSetup)).Produces<AccountingSaveResponse>().ProducesValidationProblem().ProducesProblem(409);
        Write(group.MapPost("/accounts", CreateAccounts)).Produces<AccountingSaveResponse>().ProducesValidationProblem().ProducesProblem(409);
        Write(group.MapPut("/accounts/{id:guid}", UpdateAccount)).Produces<AccountingSaveResponse>().ProducesValidationProblem().ProducesProblem(404).ProducesProblem(409);
        Write(group.MapPost("/accounts/{id:guid}/archive", ArchiveAccount)).Produces<AccountingSaveResponse>().ProducesValidationProblem().ProducesProblem(404).ProducesProblem(409);
    }
    private static RouteHandlerBuilder Write(RouteHandlerBuilder builder) => builder.RequireAuthorization("AccountingConfigurationManage").WithMetadata(WorkbenchAntiforgeryMetadata.Instance);
    private static AccountingAccountResponse Response(AccountingAccount account) => new(account.Id, account.Code, account.Name, account.Type, account.Purpose, account.Description, account.ArchivedAtUtc.HasValue, account.Version.ToString("D"));
    private static async Task<IResult> ReadSetup(WorkbenchDbContext database, CancellationToken cancellationToken)
    {
        await using var transaction = await database.Database.BeginTransactionAsync(IsolationLevel.Serializable, cancellationToken);
        var row = await database.AccountingConfigurations.AsNoTracking().SingleOrDefaultAsync(cancellationToken);
        var accounts = await database.AccountingAccounts.AsNoTracking().ToListAsync(cancellationToken);
        var result = AccountingInput.Setup(row is null ? AccountingCatalog.Empty : JsonSerializer.Deserialize<AccountingConfiguration>(row.Payload, JsonOptions)!,
            (row?.Version ?? Guid.Empty).ToString("D"), accounts.Select(Response).ToArray());
        await transaction.CommitAsync(cancellationToken);
        return Results.Ok(result);
    }
    private sealed record AccountCursor(string Code, Guid Id, string Binding);
    private static async Task<IResult> Browse(string? cursor, string? query, bool? includeArchived, WorkbenchDbContext database, CancellationToken cancellationToken)
    {
        query = string.IsNullOrWhiteSpace(query) ? null : query.Trim();
        if (query?.Length > 200 || query?.Any(char.IsControl) == true) return Problem(400, "Use up to 200 characters for search.");
        var binding = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes($"{database.TenantContext.RequireTenantId():D}:{includeArchived == true}:{query}")));
        AccountCursor? boundary = null;
        if (cursor is not null)
        {
            try { if (cursor.Length > 1024) return Problem(400, "Refresh the account list."); boundary = JsonSerializer.Deserialize<AccountCursor>(Convert.FromBase64String(cursor), JsonOptions); }
            catch (Exception e) when (e is FormatException or JsonException) { return Problem(400, "Refresh the account list."); }
            if (boundary is null || boundary.Binding != binding || boundary.Id == Guid.Empty || string.IsNullOrEmpty(boundary.Code) || boundary.Code.Length > 32) return Problem(400, "Refresh the account list.");
        }
        var code = boundary?.Code; var id = boundary?.Id;
        var rows = await database.AccountingAccounts.FromSql($"SELECT * FROM Accounting.Accounts WHERE ({includeArchived == true}=1 OR ArchivedAtUtc IS NULL) AND ({code} IS NULL OR Code>{code} OR (Code={code} AND Id>{id})) AND ({query} IS NULL OR CHARINDEX({query},Name)>0 OR CHARINDEX(UPPER({query}),Code)>0)")
            .AsNoTracking().OrderBy(a => a.Code).ThenBy(a => a.Id).Take(51).ToListAsync(cancellationToken);
        var next = rows.Count > 50 ? Convert.ToBase64String(JsonSerializer.SerializeToUtf8Bytes(new AccountCursor(rows[49].Code, rows[49].Id, binding), JsonOptions)) : null;
        return Results.Ok(new AccountingAccountPage(rows.Take(50).Select(Response).ToArray(), next));
    }
    private static async Task<IResult> SaveSetup(SaveAccountingConfigurationRequest request, WorkbenchDbContext database, RequestActor actor, CancellationToken ct)
    {
        var errors = AccountingInput.Validate(request.Configuration);
        if (errors.Count > 0) return Results.ValidationProblem(errors);
        return await Save(database, actor, request.RequestId, "Configure", null, request.ExpectedVersion, AccountingInput.Normalize(request.Configuration), ct);
    }
    private static Task<IResult> CreateAccounts(CreateAccountingAccountsRequest request, WorkbenchDbContext database, RequestActor actor, CancellationToken ct)
    {
        var errors = AccountingInput.ValidateAccounts(request.Accounts);
        return errors.Count > 0 ? Task.FromResult<IResult>(Results.ValidationProblem(errors)) : Save(database, actor, request.RequestId, "CreateAccounts", null, null, request.Accounts.Select(AccountingInput.Normalize).ToArray(), ct);
    }
    private static async Task<IResult> UpdateAccount(Guid id, UpdateAccountingAccountRequest request, WorkbenchDbContext database, RequestActor actor, CancellationToken ct)
    {
        var content = new AccountingAccountContent(request.Code, request.Name, "Asset", "General", request.Description);
        var errors = AccountingInput.ValidateAccounts([content]);
        if (errors.Count > 0) return Results.ValidationProblem(errors);
        content = AccountingInput.Normalize(content);
        return await Save(database, actor, request.RequestId, "UpdateAccount", id, request.ExpectedVersion, new { content.Code, content.Name, content.Description }, ct);
    }
    private static Task<IResult> ArchiveAccount(Guid id, ArchiveAccountingAccountRequest request, WorkbenchDbContext database, RequestActor actor, CancellationToken ct) =>
        Save(database, actor, request.RequestId, "ArchiveAccount", id, request.ExpectedVersion, new { request.IsArchived }, ct);
    private static async Task<IResult> Save(WorkbenchDbContext database, RequestActor actor, Guid requestId, string operation, Guid? id, string? expected, object payload, CancellationToken ct)
    {
        Guid? version = null;
        if (requestId == Guid.Empty || (expected is null && operation != "CreateAccounts")) return Problem(400, "A request identifier and current version are required.");
        if (expected is not null) { if (!Guid.TryParseExact(expected, "D", out var parsed)) return Problem(400, "A valid current version is required."); version = parsed; }
        var json = JsonSerializer.Serialize(payload, JsonOptions);
        if (Encoding.UTF8.GetByteCount(json) > 262144) return Problem(413, "The accounting request is too large.");
        await database.Database.OpenConnectionAsync(ct);
        try
        {
            await using var command = new SqlCommand("[Accounting].[Save]", (SqlConnection)database.Database.GetDbConnection()) { CommandType = CommandType.StoredProcedure };
            command.Parameters.AddWithValue("@ActorId", actor.UserId); command.Parameters.AddWithValue("@SessionId", actor.SessionId);
            command.Parameters.AddWithValue("@RequestId", requestId); command.Parameters.AddWithValue("@Operation", operation);
            command.Parameters.Add(new SqlParameter("@Id", SqlDbType.UniqueIdentifier) { Value = (object?)id ?? DBNull.Value });
            command.Parameters.Add(new SqlParameter("@ExpectedVersion", SqlDbType.UniqueIdentifier) { Value = (object?)version ?? DBNull.Value });
            command.Parameters.Add(new SqlParameter("@Payload", SqlDbType.NVarChar, -1) { Value = json });
            await using var reader = await command.ExecuteReaderAsync(ct);
            if (!await reader.ReadAsync(ct)) throw new InvalidOperationException("Accounting command returned no receipt.");
            return Results.Ok(new AccountingSaveResponse(requestId, reader.GetGuid(reader.GetOrdinal("SavedVersion")).ToString("D"),
                JsonSerializer.Deserialize<Guid[]>(reader.GetString(reader.GetOrdinal("AccountIdsJson")), JsonOptions)!));
        }
        catch (SqlException e) when (e.Number is 50900 or 50903 or 50904 or 50909 or 2601 or 2627 or 1205)
        {
            return e.Number switch
            {
                50900 => Problem(400, "Review the accounting values and account mappings."),
                50903 => Problem(403, "Current accounting authority is required."),
                50904 => Problem(404, "The account is unavailable."),
                _ => Problem(409, "The saved state changed, the code is reserved, or this request conflicts. Reload and review your changes.")
            };
        }
        finally { await database.Database.CloseConnectionAsync(); }
    }
    private static IResult Problem(int status, string title) => Results.Problem(statusCode: status, title: title);
}
