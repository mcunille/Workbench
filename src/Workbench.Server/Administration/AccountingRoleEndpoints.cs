// Copyright (c) 2026 The White Stag Collection.
using System.Data;
using System.Text.Json;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Workbench.Server.Authorization;
using Workbench.Server.Http;
using Workbench.Server.Persistence;
using Workbench.Server.Security;
namespace Workbench.Server.Administration;

public sealed record AccountingRoleResponse(Guid Id, string Name, string[] Permissions);
public sealed record AccountingRoleAssignmentResponse(Guid UserId, Guid[] RoleIds, string Version);
public sealed record AccountingRoleAssignmentRequest(Guid RequestId, string ExpectedVersion, Guid[] RoleIds);

public static class AccountingRoleEndpoints
{
    public static IEndpointRouteBuilder MapAccountingRoleAdministration(this IEndpointRouteBuilder endpoints)
    {
        var group = endpoints.MapGroup("/api/beta/tenant").WithTags("Tenant users")
            .RequireAuthorization(WorkbenchPermissions.TenantUsersManage);
        group.MapGet("/accounting-roles", CatalogAsync).Produces<AccountingRoleResponse[]>();
        group.MapGet("/users/{userId:guid}/accounting-roles", ReadAsync)
            .Produces<AccountingRoleAssignmentResponse>().Produces(404);
        group.MapPost("/users/{userId:guid}/accounting-roles", SaveAsync)
            .WithMetadata(WorkbenchAntiforgeryMetadata.Instance)
            .Produces<AccountingRoleAssignmentResponse>().ProducesProblem(400).Produces(403).Produces(404).ProducesProblem(409);
        return endpoints;
    }

    private static async Task<IResult> CatalogAsync(WorkbenchDbContext database, HttpContext context, CancellationToken cancellationToken)
    {
        context.Response.Headers.CacheControl = "private, no-store";
        await database.Database.OpenConnectionAsync(cancellationToken);
        await using var command = database.Database.GetDbConnection().CreateCommand();
        command.CommandText = "SELECT RoleId,Kind FROM Administration.AccountingRoles ORDER BY Kind";
        var roles = new List<AccountingRoleResponse>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            var admin = reader.GetString(1) == "Administrator";
            roles.Add(new(reader.GetGuid(0), admin ? "Accounting administrator" : "Accounting reader",
                admin ? WorkbenchPermissions.AccountingAdministratorPermissions : WorkbenchPermissions.AccountingReaderPermissions));
        }
        return TypedResults.Ok(roles.ToArray());
    }

    private static async Task<IResult> ReadAsync(Guid userId, WorkbenchDbContext database, HttpContext context, CancellationToken cancellationToken)
    {
        context.Response.Headers.CacheControl = "private, no-store";
        if (!await database.Users.AnyAsync(user => user.Id == userId, cancellationToken)) return TypedResults.NotFound();
        await database.Database.OpenConnectionAsync(cancellationToken);
        await using var command = database.Database.GetDbConnection().CreateCommand();
        await using var transaction = await database.Database.BeginTransactionAsync(IsolationLevel.Serializable, cancellationToken);
        command.Transaction = transaction.GetDbTransaction();
        command.CommandText = """
            SELECT COALESCE((SELECT Version FROM Administration.AccountingRoleAssignments WHERE UserId=@UserId),CONVERT(uniqueidentifier,'00000000-0000-0000-0000-000000000000'));
            SELECT m.RoleId FROM [Identity].UserRoles m JOIN Administration.AccountingRoles r ON r.TenantId=m.TenantId AND r.RoleId=m.RoleId WHERE m.UserId=@UserId ORDER BY m.RoleId;
            """;
        command.Parameters.Add(new SqlParameter("@UserId", userId));
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        await reader.ReadAsync(cancellationToken);
        var version = reader.GetGuid(0);
        await reader.NextResultAsync(cancellationToken);
        var roles = new List<Guid>();
        while (await reader.ReadAsync(cancellationToken)) roles.Add(reader.GetGuid(0));
        return TypedResults.Ok(new AccountingRoleAssignmentResponse(userId, roles.ToArray(), version.ToString()));
    }

    private static async Task<IResult> SaveAsync(Guid userId, AccountingRoleAssignmentRequest request, RequestActor actor,
        WorkbenchDbContext database, HttpContext context, CancellationToken cancellationToken)
    {
        context.Response.Headers.CacheControl = "private, no-store";
        if (request.RequestId == Guid.Empty || !Guid.TryParseExact(request.ExpectedVersion, "D", out var version) ||
            request.RoleIds is null || request.RoleIds.Length > 2 || request.RoleIds.Distinct().Count() != request.RoleIds.Length)
            return ApiProblemResults.InvalidRequest("Provide a request ID, current version, and distinct accounting role IDs.");
        await database.Database.OpenConnectionAsync(cancellationToken);
        await using var command = database.Database.GetDbConnection().CreateCommand();
        command.CommandText = "Administration.AssignAccountingRoles";
        command.CommandType = CommandType.StoredProcedure;
        command.Parameters.Add(new SqlParameter("@ActorId", actor.UserId));
        command.Parameters.Add(new SqlParameter("@SessionId", actor.SessionId));
        command.Parameters.Add(new SqlParameter("@UserId", userId));
        command.Parameters.Add(new SqlParameter("@RequestId", request.RequestId));
        command.Parameters.Add(new SqlParameter("@ExpectedVersion", version));
        command.Parameters.Add(new SqlParameter("@RoleIds", SqlDbType.NVarChar, -1) { Value = JsonSerializer.Serialize(request.RoleIds.Order()) });
        try
        {
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            await reader.ReadAsync(cancellationToken);
            return TypedResults.Ok(new AccountingRoleAssignmentResponse(userId,
                JsonSerializer.Deserialize<Guid[]>(reader.GetString(1))!, reader.GetGuid(0).ToString()));
        }
        catch (SqlException exception) when (exception.Number is >= 50900 and <= 50909)
        {
            return exception.Number switch
            {
                50903 => Results.Forbid(),
                50904 => TypedResults.NotFound(),
                50909 => Results.Problem(statusCode: 409, title: "Accounting roles changed or the request ID was reused. Reload and reconcile your changes."),
                _ => ApiProblemResults.InvalidRequest("Select only the available accounting roles for an enabled user.")
            };
        }
    }
}
