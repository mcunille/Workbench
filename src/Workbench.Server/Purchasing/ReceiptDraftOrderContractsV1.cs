// Copyright (c) 2026 The White Stag Collection.

using System.Text.Json.Serialization;

namespace Workbench.Server.Purchasing;

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
internal sealed record ReceiptDraftEntryV1(
    [property: JsonRequired] Guid Id,
    [property: JsonRequired] string? Description,
    [property: JsonRequired] string? Notes,
    [property: JsonRequired] string? SourceLink,
    [property: JsonRequired] string? IndicativePrice);

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
internal sealed record ReceiptDraftContentV1(
    [property: JsonRequired] string? Title,
    [property: JsonRequired] string? SupplierName,
    [property: JsonRequired] string? Currency,
    [property: JsonRequired] string? Notes,
    [property: JsonRequired] IReadOnlyList<string> SourceLinks,
    [property: JsonRequired] IReadOnlyList<ReceiptDraftEntryV1> Entries);
