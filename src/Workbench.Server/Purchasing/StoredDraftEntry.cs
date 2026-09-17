// Copyright (c) 2026 The White Stag Collection.
using System.Text.Json.Serialization;
namespace Workbench.Server.Purchasing;

// Storage-only projection for content schemas 1 and 2. Missing quote fields in schema 1 stay unknown.
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
internal sealed record StoredDraftEntry(Guid Id, string? Description, string? Notes, string? SourceLink,
    string? IndicativePrice, string? Quantity, string? UnitOfMeasure, string? UnitPrice, string? PricingUnit,
    string? PricePerQuantity, string? PricingQuantity, string? SupplierSku, string? ItemType);
