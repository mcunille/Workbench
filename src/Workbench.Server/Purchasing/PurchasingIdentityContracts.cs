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

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record DraftContentV2(
    [property: JsonRequired, MaxLength(200)] string? Title,
    [property: JsonRequired, MaxLength(200)] string? SupplierName,
    [property: JsonRequired, MaxLength(3)] string? Currency,
    [property: JsonRequired, MaxLength(10000)] string? Notes,
    [property: JsonRequired, MaxLength(20)] IReadOnlyList<string> SourceLinks,
    [property: JsonRequired, MaxLength(100)] IReadOnlyList<DraftEntry> Entries,
    [property: JsonRequired] Guid? SupplierId,
    [property: JsonRequired, MaxLength(200)] string? SupplierContactName,
    [property: JsonRequired, MaxLength(254)] string? SupplierEmail,
    [property: JsonRequired, MaxLength(100)] string? SupplierPhone,
    [property: JsonRequired, MaxLength(2048)] string? SupplierWebsite,
    [property: JsonRequired, MaxLength(2000)] string? SupplierPostalAddress,
    [property: JsonRequired, MaxLength(200)] string? SupplierOrderReference,
    [property: JsonRequired, MaxLength(200)] string? Platform);

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record CreateDraftOrderRequestV2([property: JsonRequired] Guid RequestId, [property: JsonRequired] DraftContentV2 Draft);
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record UpdateDraftOrderRequestV2([property: JsonRequired] Guid RequestId,
    [property: JsonRequired] string ExpectedVersion, [property: JsonRequired] DraftContentV2 Draft);
public sealed record DraftOrderResponseV2(Guid Id, DraftContentV2 Draft, string CreatedAtUtc, string UpdatedAtUtc,
    string Version, string PoReference, bool SupplierIsArchived);
public sealed record DraftOrderSummaryV2(Guid Id, string? Title, string? SupplierName, string UpdatedAtUtc,
    string PoReference, string? SupplierOrderReference, string? Platform);
public sealed record DraftOrderPageResponseV2(IReadOnlyList<DraftOrderSummaryV2> Items, string? NextCursor);

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
