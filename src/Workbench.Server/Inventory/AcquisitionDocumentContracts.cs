// Copyright (c) 2026 The White Stag Collection.

using System.Text.Json.Serialization;

namespace Workbench.Server.Inventory;

public sealed record AcquisitionDocumentResponse(Guid Id, string Label, string MediaType, string Extension,
    long Length, DateTimeOffset CreatedAtUtc, string Version, bool Unavailable);
public sealed record AcquisitionDocumentsResponse(AcquisitionDocumentResponse[] Documents, string ItemVersion,
    string AcquisitionVersion);
public sealed record AcquisitionDocumentOperationResponse(Guid RequestId, string State, Guid? DocumentId,
    string? ItemVersion, string? AcquisitionVersion);

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record ChangeAcquisitionDocumentRequest(Guid RequestId, string? ExpectedItemVersion,
    string? ExpectedAcquisitionVersion, string? ExpectedDocumentVersion, string? Label);
