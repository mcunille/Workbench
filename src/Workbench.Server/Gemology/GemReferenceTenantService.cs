// Copyright (c) 2026 The White Stag Collection.

using System.Data;
using System.Text.Json;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Workbench.Server.Persistence;

namespace Workbench.Server.Gemology;

public enum GemReferenceTenantWriteFailure { Validation, NotFound, Conflict, DuplicateIdentity, NeedsReview, Authority, Busy }
public sealed record GemReferenceTenantWriteResult(GemReferenceDetailResponse? Detail, GemReferenceTenantWriteFailure? Failure,
    string Code, int StatusCode, IReadOnlyDictionary<string, string[]> Errors, GemReferenceDetailResponse? Current)
{
    public bool Success => Failure is null;
}

public sealed class GemReferenceTenantService(WorkbenchDbContext database)
{
    public Task<GemReferenceTenantWriteResult> CreateAsync(Guid actorId, GemReferenceContent content, CancellationToken ct) =>
        WriteAsync(actorId, content.Id, Operation.Create, content, null, null, false, null, ct);
    public Task<GemReferenceTenantWriteResult> UpdateAsync(Guid actorId, Guid id, GemReferenceContent content, GemReferenceEffectiveVersion expected, CancellationToken ct) =>
        WriteAsync(actorId, id, Operation.Update, content, null, null, false, expected, ct);
    public Task<GemReferenceTenantWriteResult> SaveOverridesAsync(Guid actorId, Guid id, IReadOnlyDictionary<string, GemReferenceFieldOverride> overrides, GemReferenceEffectiveVersion expected, CancellationToken ct) =>
        WriteAsync(actorId, id, Operation.Overrides, null, overrides, null, false, expected, ct);
    public Task<GemReferenceTenantWriteResult> ResetAsync(Guid actorId, Guid id, string? field, GemReferenceEffectiveVersion expected, CancellationToken ct) =>
        WriteAsync(actorId, id, Operation.Reset, null, null, field, false, expected, ct);
    public Task<GemReferenceTenantWriteResult> SetArchivedAsync(Guid actorId, Guid id, bool archived, GemReferenceEffectiveVersion expected, CancellationToken ct) =>
        WriteAsync(actorId, id, Operation.Archive, null, null, null, archived, expected, ct);

    private enum Operation { Create, Update, Overrides, Reset, Archive }
    private static GemReferenceTenantWriteResult Failure(GemReferenceTenantWriteFailure failure, string code, int status,
        GemReferenceDetailResponse? current = null, IReadOnlyDictionary<string, string[]>? errors = null) =>
        new(null, failure, code, status, errors ?? new Dictionary<string, string[]>(), current);

    private async Task<GemReferenceTenantWriteResult> WriteAsync(Guid actorId, Guid id, Operation operation,
        GemReferenceContent? content, IReadOnlyDictionary<string, GemReferenceFieldOverride>? overrides,
        string? field, bool archived, GemReferenceEffectiveVersion? expected, CancellationToken ct)
    {
        if (database.TenantContext.TenantId is not { } tenantId)
            return Failure(GemReferenceTenantWriteFailure.Authority, "forbidden", 403);
        await using var transaction = await database.Database.BeginTransactionAsync(ct);
        var connection = (SqlConnection)database.Database.GetDbConnection();
        var sqlTransaction = (SqlTransaction)transaction.GetDbTransaction();
        GemReferenceDetailResponse? current = null;
        async Task<GemReferenceTenantWriteResult> Reject(GemReferenceTenantWriteResult result)
        {
            await transaction.RollbackAsync(CancellationToken.None);
            return result;
        }
        try
        {
            await GemReferenceEffectiveReadService.LockAsync(connection, sqlTransaction, tenantId, "Exclusive", ct);
            // Recheck membership under the same transaction, including the SQL tenant proof, before returning current data.
            await using (var authority = new SqlCommand("""
                SELECT COUNT(*) FROM [Identity].Users u WITH(HOLDLOCK)
                JOIN Tenancy.Tenants t WITH(HOLDLOCK) ON t.Id=u.TenantId
                WHERE u.TenantId=@tenant AND u.Id=@actor AND u.State=1 AND t.IsEnabled=1
                    AND EXISTS(SELECT 1 FROM Security.fn_tenant_access(@tenant));
                """, connection, sqlTransaction))
            {
                authority.Parameters.Add("@tenant", SqlDbType.UniqueIdentifier).Value = tenantId;
                authority.Parameters.Add("@actor", SqlDbType.UniqueIdentifier).Value = actorId;
                if ((int)(await authority.ExecuteScalarAsync(ct))! != 1)
                    return await Reject(Failure(GemReferenceTenantWriteFailure.Authority, "forbidden", 403));
            }
            var snapshot = await GemReferenceEffectiveReadService.ReadSnapshotAsync(connection, sqlTransaction, tenantId, ct);
            var sharedWrite = operation is Operation.Overrides or Operation.Reset;
            var origin = sharedWrite ? "workbench" : "tenant";
            current = snapshot.SingleOrDefault(entry => entry.Id == id && entry.Origin == origin);
            if (operation == Operation.Create)
            {
                var reserved = snapshot.SingleOrDefault(entry => entry.Id == id && entry.Origin == "workbench");
                if (reserved is not null)
                    return await Reject(Failure(GemReferenceTenantWriteFailure.DuplicateIdentity, "duplicate_identity", 409, reserved,
                        new Dictionary<string, string[]> { ["id"] = ["A Workbench reference already uses this ID."] }));
                if (current is not null) return await Reject(Failure(GemReferenceTenantWriteFailure.Conflict, "stale_entry", 409, current));
            }
            else
            {
                if (current is null)
                    return await Reject(Failure(GemReferenceTenantWriteFailure.NotFound, "not_found", 404));
                if (!TryVersion(expected, out var canonical))
                    return await Reject(Failure(GemReferenceTenantWriteFailure.Validation, "validation_failed", 400, current,
                        new Dictionary<string, string[]> { ["effectiveVersion"] = ["Provide the current composite reference version."] }));
                if (canonical != current.EffectiveVersion)
                    return await Reject(Failure(GemReferenceTenantWriteFailure.Conflict, "stale_entry", 409, current));
                expected = canonical;
            }
            var today = DateOnly.FromDateTime(DateTime.UtcNow);
            Dictionary<string, string[]> errors;
            GemReferenceDetailResponse candidate;
            if (sharedWrite)
            {
                if (operation == Operation.Reset)
                {
                    if (field is not null && !GemReferenceInput.Fields.Contains(field))
                        return await Reject(Failure(GemReferenceTenantWriteFailure.Validation, "validation_failed", 400, current,
                            new Dictionary<string, string[]> { ["field"] = ["Select a reference field to reset."] }));
                    var reset = current!.Overrides.ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.Ordinal);
                    if (field is null) reset.Clear(); else reset.Remove(field);
                    overrides = reset;
                }
                if (overrides is null)
                    return await Reject(Failure(GemReferenceTenantWriteFailure.Validation, "validation_failed", 400, current,
                        new Dictionary<string, string[]> { ["overrides"] = ["Provide the complete desired field choices."] }));
                errors = GemReferenceTenantInput.ValidateOverrides(overrides, today);
                if (errors.Count != 0) return await Reject(Failure(GemReferenceTenantWriteFailure.Validation, "validation_failed", 400, current, errors));
                candidate = GemReferenceEffectiveProjection.Resolve(current!.WorkbenchContent!, current.EffectiveVersion!.SharedRowVersion!,
                    overrides, current.EffectiveVersion.TenantRowVersion, today);
                overrides = NormalizeOverrides(overrides, candidate);
            }
            else if (operation == Operation.Archive)
                candidate = current! with { IsArchived = archived };
            else
            {
                errors = GemReferenceTenantInput.Validate(content!, today);
                if (content!.Id != id) errors["id"] = ["Content ID must match the requested entry."];
                if (errors.Count != 0) return await Reject(Failure(GemReferenceTenantWriteFailure.Validation, "validation_failed", 400, current, errors));
                content = GemReferenceInput.Normalize(content);
                candidate = GemReferenceEffectiveProjection.ResolveTenant(content, current?.RowVersion ?? "", current?.IsArchived ?? false, today);
            }
            candidate = GemReferenceEffectiveReadService.FlagDuplicates(snapshot.Where(entry => entry.Id != id || entry.Origin != origin).Append(candidate))
                .Single(entry => entry.Id == id && entry.Origin == origin);
            // Archive is available to remove an addition collision. Every other save must leave a reconciled final candidate.
            if (!(operation == Operation.Archive && archived) && candidate.NeedsReview)
            {
                var duplicate = candidate.ReviewReasons.ContainsKey("identity");
                var unreconciled = current?.NeedsReview == true;
                return await Reject(Failure(duplicate ? GemReferenceTenantWriteFailure.DuplicateIdentity :
                        unreconciled ? GemReferenceTenantWriteFailure.NeedsReview : GemReferenceTenantWriteFailure.Validation,
                    duplicate ? "duplicate_identity" : unreconciled ? "needs_review" : "validation_failed",
                    duplicate || unreconciled ? 409 : 400, current, candidate.ReviewReasons));
            }
            var procedure = sharedWrite ? "SaveTenantOverrides" : operation == Operation.Archive ? "SetTenantEntryArchive" : "SaveTenantEntry";
            await using (var command = new SqlCommand("Gemology." + procedure, connection, sqlTransaction) { CommandType = CommandType.StoredProcedure })
            {
                command.Parameters.Add("@ActorId", SqlDbType.UniqueIdentifier).Value = actorId;
                command.Parameters.Add("@EntryId", SqlDbType.UniqueIdentifier).Value = id;
                command.Parameters.Add("@ExpectedTenantRowVersion", SqlDbType.VarBinary, -1).Value =
                    (object?)GemReferenceCurationSql.Version(expected?.TenantRowVersion) ?? DBNull.Value;
                if (sharedWrite)
                {
                    command.Parameters.Add("@ExpectedSharedRowVersion", SqlDbType.VarBinary, -1).Value = GemReferenceCurationSql.Version(expected!.SharedRowVersion)!;
                    command.Parameters.Add("@OverridesJson", SqlDbType.NVarChar, -1).Value = JsonSerializer.Serialize(overrides, GemReferenceCurationSql.Json);
                }
                else if (operation == Operation.Archive) command.Parameters.Add("@IsArchived", SqlDbType.Bit).Value = archived;
                else command.Parameters.Add("@ContentJson", SqlDbType.NVarChar, -1).Value = JsonSerializer.Serialize(content, GemReferenceCurationSql.Json);
                await command.ExecuteNonQueryAsync(ct);
            }
            var saved = (await GemReferenceEffectiveReadService.ReadSnapshotAsync(connection, sqlTransaction, tenantId, ct))
                .Single(entry => entry.Id == id && entry.Origin == origin);
            await transaction.CommitAsync(ct);
            return new(saved, null, "saved", 200, new Dictionary<string, string[]>(), null);
        }
        catch (SqlException error) when (error.Number is 50042 or >= 50051 and <= 50056)
        {
            var result = error.Number switch
            {
                50051 => Failure(GemReferenceTenantWriteFailure.Authority, "forbidden", 403),
                50042 or 50052 => Failure(GemReferenceTenantWriteFailure.Busy, "reference_busy", 409),
                50053 => Failure(GemReferenceTenantWriteFailure.Validation, "validation_failed", 400, current,
                    new Dictionary<string, string[]> { ["entry"] = ["Review the reference content and field choices."] }),
                50054 => Failure(GemReferenceTenantWriteFailure.Conflict, "stale_entry", 409, current),
                50055 => Failure(GemReferenceTenantWriteFailure.NotFound, "not_found", 404),
                _ => Failure(GemReferenceTenantWriteFailure.DuplicateIdentity, "duplicate_identity", 409, current),
            };
            return await Reject(result);
        }
        catch
        {
            await transaction.RollbackAsync(CancellationToken.None);
            throw;
        }
    }

    private static bool TryVersion(GemReferenceEffectiveVersion? version, out GemReferenceEffectiveVersion? canonical)
    {
        canonical = null;
        if (version is null) return false;
        try
        {
            string? Normalize(string? value)
            {
                if (value is null) return null;
                var bytes = Convert.FromBase64String(value);
                return bytes.Length == 8 ? Convert.ToBase64String(bytes) : throw new FormatException();
            }
            canonical = new(Normalize(version.SharedRowVersion), Normalize(version.TenantRowVersion));
            return true;
        }
        catch (FormatException) { return false; }
    }

    private static IReadOnlyDictionary<string, GemReferenceFieldOverride> NormalizeOverrides(
        IReadOnlyDictionary<string, GemReferenceFieldOverride> choices, GemReferenceDetailResponse candidate)
    {
        var effective = GemReferenceEffectiveReadService.Content(candidate);
        object? Value(string field) => field switch
        {
            "materialKind" => effective.MaterialKind,
            "commonName" => effective.CommonName,
            "group" => effective.Group,
            "species" => effective.Species,
            "variety" => effective.Variety,
            "description" => effective.Description,
            "aliases" => effective.Aliases,
            "notableLocality" => effective.NotableLocality,
            _ => null,
        };
        return choices.Where(pair => pair.Value.State != "inherit").ToDictionary(pair => pair.Key,
            pair => pair.Value.State == "clear" ? new GemReferenceFieldOverride("clear", null, []) :
                new GemReferenceFieldOverride("replace", JsonSerializer.SerializeToElement(Value(pair.Key), GemReferenceCurationSql.Json),
                    effective.Sources.Where(source => source.Field == pair.Key).ToArray()), StringComparer.Ordinal);
    }
}
