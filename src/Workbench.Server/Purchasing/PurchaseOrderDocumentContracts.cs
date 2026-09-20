// Copyright (c) 2026 The White Stag Collection.

using System.Text.Json.Serialization;

namespace Workbench.Server.Purchasing;

public sealed record UploadPurchaseOrderDocumentRequest(IFormFile File, string Label, Guid RequestId, string ExpectedOrderVersion);

public sealed record PurchaseOrderDocumentResponse(Guid Id, string Label, string MediaType, string Extension,
    long Length, DateTimeOffset CreatedAtUtc, string Version, bool Unavailable);
public sealed record PurchaseOrderDocumentsResponse(PurchaseOrderDocumentResponse[] Documents, string OrderVersion);
public sealed record PurchaseOrderDocumentOperationResponse(Guid RequestId, string State, Guid? DocumentId,
    string? OrderVersion);

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record ChangePurchaseOrderDocumentRequest(Guid RequestId, string? ExpectedOrderVersion, string? ExpectedDocumentVersion, string? Label);
