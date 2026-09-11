// Copyright (c) 2026 The White Stag Collection.

using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Workbench.Server.Http;
using Workbench.Server.Storage;

namespace Workbench.Server.Inventory;

public static class AcquisitionDocumentEndpoints
{
    public const long MaximumRequestBytes = 10 * 1024 * 1024 + 64 * 1024;

    public static void MapAcquisitionDocuments(this RouteGroupBuilder inventory)
    {
        var group = inventory.MapGroup("/{id:guid}/acquisition/{acquisitionId:guid}/documents");
        group.MapGet("", ListAsync).Produces<AcquisitionDocumentsResponse>().ProducesProblem(404).ProducesProblem(503);
        group.MapGet("/operations/{requestId:guid}", OperationAsync)
            .Produces<AcquisitionDocumentOperationResponse>().ProducesProblem(404).ProducesProblem(503);
        group.MapGet("/{documentId:guid}/download", DownloadAsync)
            .Produces<byte[]>(contentType: "application/octet-stream").ProducesProblem(404).ProducesProblem(410).ProducesProblem(503);
        var upload = group.MapPost("", UploadAsync)
            .WithMetadata(WorkbenchAntiforgeryMetadata.Instance, new DocumentUploadMetadata());
        var rename = group.MapPut("/{documentId:guid}", RenameAsync).WithMetadata(WorkbenchAntiforgeryMetadata.Instance);
        var remove = group.MapDelete("/{documentId:guid}", RemoveAsync).WithMetadata(WorkbenchAntiforgeryMetadata.Instance);
        foreach (var endpoint in new[] { upload, rename, remove })
        {
            endpoint.Produces<AcquisitionDocumentOperationResponse>();
            foreach (var status in new[] { 400, 401, 403, 404, 409, 413, 415, 422, 503 }) endpoint.ProducesProblem(status);
        }
    }

    private static Task<IResult> ListAsync(Guid id, Guid acquisitionId, AcquisitionDocumentService service, CancellationToken cancellationToken) =>
        ExecuteAsync(async () => Results.Ok(await service.ListAsync(id, acquisitionId, cancellationToken)));

    private static Task<IResult> OperationAsync(Guid id, Guid acquisitionId, Guid requestId, AcquisitionDocumentService service, CancellationToken cancellationToken) =>
        ExecuteAsync(async () => Results.Ok(await service.OperationAsync(id, acquisitionId, requestId, cancellationToken)));

    private static Task<IResult> UploadAsync(Guid id, Guid acquisitionId, HttpRequest request,
        AcquisitionDocumentService service, DocumentValidator validator, CancellationToken cancellationToken) => ExecuteAsync(async () =>
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(TimeSpan.FromMinutes(2));
        cancellationToken = deadline.Token;
        await service.RequireContextAsync(id, acquisitionId, cancellationToken);
        if (!request.HasFormContentType || !request.ContentType!.StartsWith("multipart/form-data", StringComparison.OrdinalIgnoreCase))
            throw new DocumentInputException(415, "Choose one PDF, JPEG, PNG or WebP document.");
        IFormCollection form;
        try { form = await request.ReadFormAsync(cancellationToken); }
        catch (Exception error) when (error is IOException or InvalidDataException)
        { throw new DocumentInputException(413, "Upload one document of 10 MiB or less with a label of 200 characters or fewer."); }
        if (form.Files.Count != 1 || form.Files[0].Name != "file" || form.Count != 4 ||
            new[] { "requestId", "expectedItemVersion", "expectedAcquisitionVersion", "label" }.Any(key => form[key].Count != 1) ||
            !Guid.TryParse(form["requestId"], out var requestId) || requestId == Guid.Empty)
            throw new DocumentInputException(400, "Provide one document, a label, a request identifier and saved item/acquisition versions.");
        var label = Label(form["label"]);
        var itemVersion = Version(form["expectedItemVersion"]);
        var acquisitionVersion = Version(form["expectedAcquisitionVersion"]);
        if (form.Files[0].Length > DocumentValidator.MaximumBytes)
            throw new DocumentInputException(413, "Choose a document of 10 MiB or less.");
        await using var source = form.Files[0].OpenReadStream();
        using var buffer = new MemoryStream();
        await BlobTransfer.CopyAsync(source, buffer, DocumentValidator.MaximumBytes, cancellationToken);
        var bytes = buffer.ToArray();
        var validated = validator.Validate(bytes);
        return Results.Ok(await service.ChangeAsync(id, acquisitionId, requestId, itemVersion, acquisitionVersion,
            null, null, label, bytes, validated, cancellationToken));
    });

    private static Task<IResult> RenameAsync(Guid id, Guid acquisitionId, Guid documentId,
        ChangeAcquisitionDocumentRequest request, AcquisitionDocumentService service, CancellationToken cancellationToken) =>
        ChangeAsync(id, acquisitionId, documentId, request, false, service, cancellationToken);

    private static Task<IResult> RemoveAsync(Guid id, Guid acquisitionId, Guid documentId,
        [FromBody] ChangeAcquisitionDocumentRequest request, AcquisitionDocumentService service, CancellationToken cancellationToken) =>
        ChangeAsync(id, acquisitionId, documentId, request, true, service, cancellationToken);

    private static Task<IResult> ChangeAsync(Guid id, Guid acquisitionId, Guid documentId,
        ChangeAcquisitionDocumentRequest request, bool remove, AcquisitionDocumentService service, CancellationToken cancellationToken) => ExecuteAsync(async () =>
    {
        await service.RequireContextAsync(id, acquisitionId, cancellationToken);
        if (request.RequestId == Guid.Empty || (remove && request.Label is not null))
            throw new DocumentInputException(400, "Provide a request identifier and the saved versions; removal does not accept a new label.");
        return Results.Ok(await service.ChangeAsync(id, acquisitionId, request.RequestId, Version(request.ExpectedItemVersion),
            Version(request.ExpectedAcquisitionVersion), documentId, Version(request.ExpectedDocumentVersion),
            remove ? null : Label(request.Label), null, null, cancellationToken));
    });

    private static Task<IResult> DownloadAsync(Guid id, Guid acquisitionId, Guid documentId, HttpResponse response,
        AcquisitionDocumentService service, CancellationToken cancellationToken) => ExecuteAsync(async () =>
    {
        var file = await service.ReadAsync(id, acquisitionId, documentId, cancellationToken);
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

public sealed class DocumentUploadMetadata;

public sealed class DocumentUploadLimitsMiddleware(RequestDelegate next)
{
    private static readonly SemaphoreSlim Capacity = new(1, 1);

    public async Task InvokeAsync(HttpContext context)
    {
        if (context.GetEndpoint()?.Metadata.GetMetadata<DocumentUploadMetadata>() is null)
        { await next(context); return; }
        if (!context.Request.Headers.ContainsKey("X-CSRF-TOKEN"))
        {
            await Results.Problem(statusCode: 400, title: "Antiforgery validation failed.").ExecuteAsync(context);
            return;
        }
        if (context.Request.ContentLength > AcquisitionDocumentEndpoints.MaximumRequestBytes)
        { await TooLarge(context); return; }
        // Admission precedes antiforgery/form parsing and all request-body buffering.
        if (!Capacity.Wait(0))
        {
            await Results.Problem(statusCode: 503, title: "Document upload is busy. Retry the same request shortly.").ExecuteAsync(context);
            return;
        }
        try
        {
            var limits = context.Features.Get<IHttpMaxRequestBodySizeFeature>();
            if (limits is { IsReadOnly: false }) limits.MaxRequestBodySize = AcquisitionDocumentEndpoints.MaximumRequestBytes;
            context.Request.EnableBuffering((int)AcquisitionDocumentEndpoints.MaximumRequestBytes + 1, AcquisitionDocumentEndpoints.MaximumRequestBytes);
            context.Features.Set<IFormFeature>(new FormFeature(context.Request, new FormOptions
            {
                MemoryBufferThreshold = (int)AcquisitionDocumentEndpoints.MaximumRequestBytes + 1,
                MultipartBodyLengthLimit = DocumentValidator.MaximumBytes,
                ValueCountLimit = 5,
                ValueLengthLimit = 1024,
                KeyLengthLimit = 32,
                MultipartHeadersLengthLimit = 4096,
                MultipartBoundaryLengthLimit = 128,
            }));
            try { await next(context); }
            catch (InvalidDataException) { await TooLarge(context); }
            catch (BadHttpRequestException error) when (error.StatusCode == 413) { await TooLarge(context); }
        }
        finally { Capacity.Release(); }
    }

    private static Task TooLarge(HttpContext context) => Results.Problem(statusCode: 413,
        title: "Upload one document of 10 MiB or less.").ExecuteAsync(context);
}
