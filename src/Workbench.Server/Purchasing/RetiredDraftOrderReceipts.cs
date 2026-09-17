// Copyright (c) 2026 The White Stag Collection.
using System.Data;
using System.Text.Json;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Workbench.Server.Authorization;
using Workbench.Server.Http;
using Workbench.Server.Persistence;

namespace Workbench.Server.Purchasing;

// These routes resolve uncertain historical saves only. They never invoke a write command.
internal static class RetiredDraftOrderReceipts
{
    public static void MapRetiredPurchaseOrderDraftReceipts(this IEndpointRouteBuilder endpoints)
    {
        foreach (var version in new[] { 1, 2, 3, 4 })
        {
            var fingerprintVersion = version;
            var path = version == 1 ? "/api/purchase-order-drafts" : $"/api/v{version}/purchase-order-drafts";
            var group = endpoints.MapGroup(path).RequireAuthorization().ExcludeFromDescription();
            group.MapPost("", (HttpContext context, WorkbenchDbContext database, RequestActor actor, CancellationToken cancellationToken) =>
                ReplayAsync(context, database, actor, "Create", null, fingerprintVersion, cancellationToken))
                .WithMetadata(WorkbenchAntiforgeryMetadata.Instance);
            group.MapPut("/{id:guid}", (Guid id, HttpContext context, WorkbenchDbContext database, RequestActor actor, CancellationToken cancellationToken) =>
                ReplayAsync(context, database, actor, "Update", id, fingerprintVersion, cancellationToken))
                .WithMetadata(WorkbenchAntiforgeryMetadata.Instance);
            group.MapDelete("/{id:guid}", (Guid id, HttpContext context, WorkbenchDbContext database, RequestActor actor, CancellationToken cancellationToken) =>
                ReplayAsync(context, database, actor, "Delete", id, 1, cancellationToken))
                .WithMetadata(WorkbenchAntiforgeryMetadata.Instance);
        }
    }

    private static async Task<IResult> ReplayAsync(HttpContext context, WorkbenchDbContext database, RequestActor actor,
        string operation, Guid? targetId, int fingerprintVersion, CancellationToken cancellationToken)
    {
        if (!context.Request.HasJsonContentType()) return Results.StatusCode(StatusCodes.Status415UnsupportedMediaType);
        Guid requestId;
        string? expectedVersion = null;
        string canonical;
        try
        {
            using var document = await JsonDocument.ParseAsync(context.Request.Body, cancellationToken: cancellationToken);
            var root = document.RootElement;
            var allowed = operation == "Create" ? new[] { "requestId", "draft" } : operation == "Delete"
                ? new[] { "requestId", "expectedVersion" } : ["requestId", "expectedVersion", "draft"];
            if (root.ValueKind != JsonValueKind.Object || root.EnumerateObject().Any(property => !allowed.Contains(property.Name, StringComparer.OrdinalIgnoreCase)) ||
                root.EnumerateObject().Select(property => property.Name).Distinct(StringComparer.OrdinalIgnoreCase).Count() != root.EnumerateObject().Count() ||
                !TryProperty(root, "requestId", out var request) || !request.TryGetGuid(out requestId) || requestId == Guid.Empty)
                return Invalid();
            if (operation != "Create")
            {
                var errors = new Dictionary<string, string[]>();
                expectedVersion = ReceiptDraftOrderInputV1.NormalizeVersion(Property(root, "expectedVersion").GetString(), errors);
                if (errors.Count > 0) return Invalid();
            }
            // Frozen normalizers preserve each historical fingerprint's field order and number formatting.
            canonical = operation == "Delete"
                ? JsonSerializer.Serialize(new { operation, targetId, expectedVersion, draft = (object?)null }, ReceiptDraftOrderInputV1.JsonOptions)
                : Canonical(fingerprintVersion, operation, targetId, expectedVersion, Property(root, "draft"));
        }
        catch (Exception exception) when (exception is JsonException or InvalidOperationException or KeyNotFoundException or FormatException or ArgumentException)
        {
            return Invalid();
        }

        await database.Database.OpenConnectionAsync(cancellationToken);
        try
        {
            await using var command = new SqlCommand("[Purchasing].[ReplayDraftOrderReceipt]", (SqlConnection)database.Database.GetDbConnection())
            { CommandType = CommandType.StoredProcedure };
            command.Parameters.Add(new SqlParameter("@RequestId", SqlDbType.UniqueIdentifier) { Value = requestId });
            command.Parameters.Add(new SqlParameter("@ActorUserId", SqlDbType.UniqueIdentifier) { Value = actor.UserId });
            command.Parameters.Add(new SqlParameter("@Operation", SqlDbType.VarChar, 6) { Value = operation });
            command.Parameters.Add(new SqlParameter("@DraftOrderId", SqlDbType.UniqueIdentifier) { Value = (object?)targetId ?? DBNull.Value });
            command.Parameters.Add(new SqlParameter("@ExpectedRowVersion", SqlDbType.VarBinary, -1) { Value = expectedVersion is null ? DBNull.Value : Convert.FromBase64String(expectedVersion) });
            command.Parameters.Add(new SqlParameter("@FingerprintVersion", SqlDbType.Int) { Value = fingerprintVersion });
            command.Parameters.Add(new SqlParameter("@CanonicalInputJson", SqlDbType.NVarChar, -1) { Value = canonical });
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            if (!await reader.ReadAsync(cancellationToken)) throw new InvalidOperationException("Receipt lookup did not return a result.");
            var response = new SaveDraftOrderResponse(reader.GetGuid(reader.GetOrdinal("RequestId")), true,
                reader.GetGuid(reader.GetOrdinal("DraftOrderId")), Convert.ToBase64String((byte[])reader["SavedVersion"]),
                DraftOrderCursor.Timestamp(reader.GetFieldValue<DateTimeOffset>(reader.GetOrdinal("CompletedAtUtc"))));
            if (operation == "Create") context.Response.Headers.Location = $"/api/beta/purchase-order-drafts/{response.DraftOrderId:D}";
            return Results.Ok(response);
        }
        catch (SqlException exception) when (exception.Number is 50400 or 50403 or 50410 or 50427)
        {
            return exception.Number switch
            {
                50403 => Problem(403, "draft_authority_required", "Current business authority is required."),
                50410 => Problem(409, "draft_request_conflict", "This request identifier was already used for different input."),
                50427 => Problem(426, "api_contract_unsupported", "Reload the application. This API contract no longer accepts new work."),
                _ => Invalid()
            };
        }
        finally { await database.Database.CloseConnectionAsync(); }
    }

    internal static string Canonical(int version, string operation, Guid? target, string? expectedVersion, JsonElement draft)
    {
        if (draft.ValueKind != JsonValueKind.Object || !TryProperty(draft, "entries", out var entries) ||
            entries.ValueKind != JsonValueKind.Array || entries.EnumerateArray().Any(entry => entry.ValueKind != JsonValueKind.Object) ||
            !TryProperty(draft, "sourceLinks", out var links) || links.ValueKind != JsonValueKind.Array ||
            links.EnumerateArray().Any(link => link.ValueKind != JsonValueKind.String)) throw new JsonException("Supply the original draft arrays.");
        T Read<T>() => draft.Deserialize<T>(ReceiptDraftOrderInputV1.JsonOptions) ?? throw new JsonException("Supply a draft.");
        return version switch
        {
            1 => ReceiptDraftOrderInputV1.Canonical(operation, target, expectedVersion, ReceiptDraftOrderInputV1.Normalize(Read<ReceiptDraftContentV1>())),
            2 => PurchasingIdentityInput.Canonical(operation, target, expectedVersion, PurchasingIdentityInput.Normalize(Read<DraftContentV2>())),
            3 => DraftOrderInputV3.Canonical(operation, target, expectedVersion, DraftOrderInputV3.Normalize(Read<DraftContentV3>())),
            4 => ReceiptDraftOrderInputV4.Canonical(operation, target, expectedVersion, ReceiptDraftOrderInputV4.Normalize(Read<ReceiptDraftContentV4>())),
            _ => throw new ArgumentOutOfRangeException(nameof(version))
        };
    }

    private static bool TryProperty(JsonElement element, string name, out JsonElement value)
    {
        foreach (var property in element.EnumerateObject())
            if (string.Equals(property.Name, name, StringComparison.OrdinalIgnoreCase)) { value = property.Value; return true; }
        value = default;
        return false;
    }
    private static JsonElement Property(JsonElement element, string name) => TryProperty(element, name, out var value)
        ? value : throw new JsonException("A required request field is missing.");

    private static IResult Invalid() => Problem(400, "draft_validation_failed", "Supply the exact original request identifier, saved version and draft.");
    private static IResult Problem(int status, string code, string title) => Results.Problem(statusCode: status,
        title: title, type: "about:blank", extensions: new Dictionary<string, object?> { ["code"] = code });
}
