// Copyright (c) 2026 The White Stag Collection.

using Workbench.Server.Inventory;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Workbench.Server.Http;
using Workbench.Server.Storage;

namespace Workbench.Server.Purchasing;

public static class PurchaseOrderDocumentEndpoints
{
    public static void MapPurchaseOrderDocuments(this RouteGroupBuilder purchases)
    {
        var group = purchases.MapGroup("/{id:guid}/documents");
        group.MapGet("", ListAsync).Produces<PurchaseOrderDocumentsResponse>().ProducesProblem(404).ProducesProblem(409).ProducesProblem(503);
        group.MapGet("/operations/{requestId:guid}", OperationAsync)
            .Produces<PurchaseOrderDocumentOperationResponse>().ProducesProblem(404).ProducesProblem(503);
        group.MapGet("/{documentId:guid}/download", DownloadAsync)
            .Produces<byte[]>(contentType: "application/octet-stream").ProducesProblem(404).ProducesProblem(410).ProducesProblem(503);
        var upload = group.MapPost("", UploadAsync)
            .WithMetadata(WorkbenchAntiforgeryMetadata.Instance, new DocumentUploadMetadata())
            .Accepts<UploadPurchaseOrderDocumentRequest>("multipart/form-data");
        var rename = group.MapPut("/{documentId:guid}", RenameAsync).WithMetadata(WorkbenchAntiforgeryMetadata.Instance);
        var remove = group.MapDelete("/{documentId:guid}", RemoveAsync).WithMetadata(WorkbenchAntiforgeryMetadata.Instance);
        foreach (var endpoint in new[] { upload, rename, remove })
        {
            endpoint.Produces<PurchaseOrderDocumentOperationResponse>();
            foreach (var status in new[] { 400, 401, 403, 404, 409, 413, 415, 422, 503 }) endpoint.ProducesProblem(status);
        }
    }

    private static Task<IResult> ListAsync(Guid id, PurchaseOrderDocumentService service, CancellationToken cancellationToken) =>
        ExecuteAsync(async () => Results.Ok(await service.ListAsync(id, cancellationToken)));

    private static Task<IResult> OperationAsync(Guid id, Guid requestId, PurchaseOrderDocumentService service, CancellationToken cancellationToken) =>
        ExecuteAsync(async () => Results.Ok(await service.OperationAsync(id, requestId, cancellationToken)));

    private static Task<IResult> UploadAsync(Guid id, HttpRequest request,
        PurchaseOrderDocumentService service, DocumentValidator validator, CancellationToken cancellationToken) => ExecuteAsync(async () =>
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(TimeSpan.FromMinutes(2));
        cancellationToken = deadline.Token;
        await service.RequireContextAsync(id, cancellationToken);
        if (!request.HasFormContentType || !request.ContentType!.StartsWith("multipart/form-data", StringComparison.OrdinalIgnoreCase))
            throw new DocumentInputException(415, "Choose one PDF, JPEG, PNG or WebP document.");
        IFormCollection form;
        try { form = await request.ReadFormAsync(cancellationToken); }
        catch (Exception error) when (error is IOException or InvalidDataException)
        { throw new DocumentInputException(413, "Upload one document of 10 MiB or less with a label of 200 characters or fewer."); }
        if (form.Files.Count != 1 || form.Files[0].Name != "file" || form.Count != 3 ||
            new[] { "requestId", "expectedOrderVersion", "label" }.Any(key => form[key].Count != 1) ||
            !Guid.TryParse(form["requestId"], out var requestId) || requestId == Guid.Empty)
            throw new DocumentInputException(400, "Provide one document, a label, a request identifier and the saved purchase order version.");
        var label = Label(form["label"]);
        var orderVersion = Version(form["expectedOrderVersion"]);
        if (form.Files[0].Length > DocumentValidator.MaximumBytes)
            throw new DocumentInputException(413, "Choose a document of 10 MiB or less.");
        await using var source = form.Files[0].OpenReadStream();
        using var buffer = new MemoryStream();
        await BlobTransfer.CopyAsync(source, buffer, DocumentValidator.MaximumBytes, cancellationToken);
        var bytes = buffer.ToArray();
        var validated = validator.Validate(bytes);
        return Results.Ok(await service.ChangeAsync(id, requestId, orderVersion,
            null, null, label, bytes, validated, cancellationToken));
    });

    private static Task<IResult> RenameAsync(Guid id, Guid documentId,
        ChangePurchaseOrderDocumentRequest request, PurchaseOrderDocumentService service, CancellationToken cancellationToken) =>
        ChangeAsync(id, documentId, request, false, service, cancellationToken);

    private static Task<IResult> RemoveAsync(Guid id, Guid documentId,
        [FromBody] ChangePurchaseOrderDocumentRequest request, PurchaseOrderDocumentService service, CancellationToken cancellationToken) =>
        ChangeAsync(id, documentId, request, true, service, cancellationToken);

    private static Task<IResult> ChangeAsync(Guid id, Guid documentId,
        ChangePurchaseOrderDocumentRequest request, bool remove, PurchaseOrderDocumentService service, CancellationToken cancellationToken) => ExecuteAsync(async () =>
    {
        await service.RequireContextAsync(id, cancellationToken);
        if (request.RequestId == Guid.Empty || (remove && request.Label is not null))
            throw new DocumentInputException(400, "Provide a request identifier and the saved versions; removal does not accept a new label.");
        return Results.Ok(await service.ChangeAsync(id, request.RequestId, Version(request.ExpectedOrderVersion), documentId, Version(request.ExpectedDocumentVersion),
            remove ? null : Label(request.Label), null, null, cancellationToken));
    });

    private static Task<IResult> DownloadAsync(Guid id, Guid documentId, HttpResponse response,
        PurchaseOrderDocumentService service, CancellationToken cancellationToken) => ExecuteAsync(async () =>
    {
        var file = await service.ReadAsync(id, documentId, cancellationToken);
        response.Headers.XContentTypeOptions = "nosniff";
        return Results.File(file.Content, file.MediaType, $"document-{documentId:N}.{file.Extension}");
    });

    private static string Label(string? value)
    {
        var label = value?.Trim();
        if (string.IsNullOrEmpty(label) || label.Length > 200)
            throw new DocumentInputException(400, "Enter a document label of 1 to 200 characters.");
        return label;
    }

    private static byte[] Version(string? value)
    {
        var bytes = new byte[8];
        if (value is null || !Convert.TryFromBase64String(value, bytes, out var count) || count != 8)
            throw new DocumentInputException(400, "Reload the saved context to obtain its current versions.");
        return bytes;
    }

    private static async Task<IResult> ExecuteAsync(Func<Task<IResult>> action)
    {
        try { return await action(); }
        catch (RecoveredFileUnavailableException)
        {
            return Results.Problem(statusCode: 410, title: "This document could not be recovered. Keep its record and contact the administrator, or add another copy.",
                extensions: new Dictionary<string, object?> { ["code"] = "file_unavailable_after_recovery" });
        }
        catch (DocumentInputException error) { return Results.Problem(statusCode: error.StatusCode, title: error.Message); }
        catch (UnauthorizedAccessException) { return Results.Problem(statusCode: 403, title: "Document access is denied."); }
        catch (Exception error) when (error is IOException or InvalidDataException or SqlException or DbUpdateException or OperationCanceledException)
        { return Results.Problem(statusCode: 503, title: "The document operation could not be confirmed. Retry the same operation or check its saved status."); }
    }
}
