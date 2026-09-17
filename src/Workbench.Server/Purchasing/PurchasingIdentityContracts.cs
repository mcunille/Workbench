// Copyright (c) 2026 The White Stag Collection.
using System.Text.Json.Serialization;
using System.ComponentModel.DataAnnotations;

namespace Workbench.Server.Purchasing;

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record SupplierContent(
    [property: JsonRequired, MaxLength(200)] string Name,
    [property: JsonRequired, MaxLength(200)] string? ContactName,
    [property: JsonRequired, MaxLength(254)] string? Email,
    [property: JsonRequired, MaxLength(100)] string? Phone,
    [property: JsonRequired, MaxLength(2048)] string? Website,
    [property: JsonRequired, MaxLength(2000)] string? PostalAddress);

public sealed record DraftOrderSummary(Guid Id, string? Title, string? SupplierName, string UpdatedAtUtc,
    string PoReference, string? SupplierOrderReference, string? Platform);
public sealed record DraftOrderPageResponse(IReadOnlyList<DraftOrderSummary> Items, string? NextCursor);

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record CreateSupplierRequest([property: JsonRequired] Guid RequestId, [property: JsonRequired] SupplierContent Supplier);
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record UpdateSupplierRequest([property: JsonRequired] Guid RequestId, [property: JsonRequired] string ExpectedVersion,
    [property: JsonRequired] SupplierContent Supplier);
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record ArchiveSupplierRequest([property: JsonRequired] Guid RequestId, [property: JsonRequired] string ExpectedVersion,
    [property: JsonRequired] bool IsArchived);
public sealed record SupplierResponse(Guid Id, SupplierContent Supplier, bool IsArchived, string CreatedAtUtc, string UpdatedAtUtc, string Version);
public sealed record SupplierPageResponse(IReadOnlyList<SupplierResponse> Items, string? NextCursor);
public sealed record SaveSupplierResponse(Guid RequestId, bool Replayed, Guid SupplierId, string SavedVersion, string CompletedAtUtc);
