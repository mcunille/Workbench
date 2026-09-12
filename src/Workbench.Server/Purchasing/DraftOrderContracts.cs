// Copyright (c) 2026 The White Stag Collection.

using System.Text.Json.Serialization;

namespace Workbench.Server.Purchasing;

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record DraftEntry(
    [property: JsonRequired] Guid Id,
    [property: JsonRequired] string? Description,
    [property: JsonRequired] string? Notes,
    [property: JsonRequired] string? SourceLink,
    [property: JsonRequired] string? IndicativePrice);

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record DraftContent(
    [property: JsonRequired] string? Title,
    [property: JsonRequired] string? SupplierName,
    [property: JsonRequired] string? Currency,
    [property: JsonRequired] string? Notes,
    [property: JsonRequired] IReadOnlyList<string> SourceLinks,
    [property: JsonRequired] IReadOnlyList<DraftEntry> Entries);

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record CreateDraftOrderRequest([property: JsonRequired] Guid RequestId, [property: JsonRequired] DraftContent Draft);

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record UpdateDraftOrderRequest([property: JsonRequired] Guid RequestId,
    [property: JsonRequired] string ExpectedVersion, [property: JsonRequired] DraftContent Draft);

public sealed record DraftOrderResponse(Guid Id, DraftContent Draft, string CreatedAtUtc, string UpdatedAtUtc, string Version);
public sealed record SaveDraftOrderResponse(Guid RequestId, bool Replayed, Guid DraftOrderId, string SavedVersion, string CompletedAtUtc);
public sealed record DraftOrderSummary(Guid Id, string? Title, string? SupplierName, string UpdatedAtUtc);
public sealed record DraftOrderPageResponse(IReadOnlyList<DraftOrderSummary> Items, string? NextCursor);
