// Copyright (c) 2026 The White Stag Collection.

using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Workbench.Server.Http;
using Workbench.Server.Storage;

namespace Workbench.Server.Inventory;

public static class ItemPhotoEndpoints
{
    public const long MaximumRequestBytes = PhotoProcessor.MaximumBytes + 64 * 1024;

    public static void MapItemPhotos(this RouteGroupBuilder group)
    {
        var upload = group.MapPut("/{id:guid}/photo", UploadAsync)
            .WithMetadata(WorkbenchAntiforgeryMetadata.Instance, new PhotoUploadMetadata())
            .Accepts<UploadItemPhotoRequest>("multipart/form-data");
        var remove = group.MapDelete("/{id:guid}/photo", RemoveAsync)
            .WithMetadata(WorkbenchAntiforgeryMetadata.Instance);
        foreach (var endpoint in new[] { upload, remove })
        {
            endpoint.Produces<ItemPhotoMutationResponse>();
            foreach (var status in new[] { 400, 401, 403, 404, 409, 413, 415, 422, 503 })
                endpoint.ProducesProblem(status);
        }
        group.MapGet("/{id:guid}/photo/{photoId:guid}/{variant}", ReadAsync)
            .Produces<byte[]>(contentType: "image/webp").ProducesProblem(404).ProducesProblem(410).ProducesProblem(503);
    }

    private static async Task<IResult> UploadAsync(Guid id, HttpRequest request, ItemPhotoService service, CancellationToken cancellationToken)
    {
        try
        {
            await service.RequireItemAsync(id, cancellationToken);
            if (!request.HasFormContentType || !request.ContentType!.StartsWith("multipart/form-data", StringComparison.OrdinalIgnoreCase))
                throw new PhotoInputException(415, "Choose a prepared JPEG, PNG, or WebP photo.");
            IFormCollection form;
            try { form = await request.ReadFormAsync(cancellationToken); }
            catch (IOException) { throw new PhotoInputException(413, "Upload one prepared photo of 4 MiB or less."); }
            catch (InvalidDataException) { throw new PhotoInputException(413, "Upload one prepared photo of 4 MiB or less."); }
            if (form.Files.Count != 1 || form.Files[0].Name != "file" || form.Count != 2 ||
                form["requestId"].Count != 1 || form["expectedVersion"].Count != 1 ||
                !Guid.TryParse(form["requestId"], out var requestId) || requestId == Guid.Empty)
                throw new PhotoInputException(400, "Provide one photo, a request identifier, and the current item version.");
            var file = form.Files[0];
            if (file.Length > PhotoProcessor.MaximumBytes)
                throw new PhotoInputException(413, "Prepare a photo of 4 MiB or less before uploading.");
            await using var source = file.OpenReadStream();
            using var bytes = new MemoryStream();
            await BlobTransfer.CopyAsync(source, bytes, PhotoProcessor.MaximumBytes, cancellationToken);
            return Results.Ok(await service.ChangeAsync(id, requestId, Version(form["expectedVersion"]!), bytes.ToArray(), cancellationToken));
        }
        catch (Exception error) when (IsPhotoError(error)) { return Failure(error); }
    }

    private static async Task<IResult> RemoveAsync(Guid id, [FromBody] RemoveItemPhotoRequest request, ItemPhotoService service, CancellationToken cancellationToken)
    {
        try
        {
            return Results.Ok(await service.ChangeAsync(id, request.RequestId, Version(request.ExpectedVersion), null, cancellationToken));
        }
        catch (Exception error) when (IsPhotoError(error)) { return Failure(error); }
    }

    private static async Task<IResult> ReadAsync(Guid id, Guid photoId, string variant, HttpResponse response,
        ItemPhotoService service, CancellationToken cancellationToken)
    {
        try
        {
            var bytes = await service.ReadAsync(id, photoId, variant, cancellationToken);
            response.Headers.XContentTypeOptions = "nosniff";
            return Results.Bytes(bytes, "image/webp");
        }
        catch (Exception error) when (IsPhotoError(error)) { return Failure(error); }
    }

    private static byte[] Version(string? value)
    {
        Span<byte> bytes = stackalloc byte[8];
        if (value is null || !Convert.TryFromBase64String(value, bytes, out var count) || count != 8)
            throw new PhotoInputException(400, "Reload the item to obtain its current version.");
        return bytes.ToArray();
    }

    private static bool IsPhotoError(Exception error) => error is PhotoInputException or IOException or InvalidDataException or UnauthorizedAccessException
        or SqlException or DbUpdateException or OperationCanceledException;
    private static IResult Failure(Exception error) => error switch
    {
        RecoveredFileUnavailableException => Results.Problem(statusCode: 410,
            title: "This photograph could not be recovered. Replace it with another copy.",
            extensions: new Dictionary<string, object?> { ["code"] = "file_unavailable_after_recovery" }),
        PhotoInputException photo => Results.Problem(statusCode: photo.StatusCode, title: photo.Message),
        UnauthorizedAccessException => Results.Problem(statusCode: 403, title: "Photo access is denied."),
        _ => Results.Problem(statusCode: 503, title: "The photo operation could not be confirmed. Retry the same operation shortly."),
    };
}

public sealed class PhotoUploadMetadata;

public sealed class PhotoUploadLimitsMiddleware(RequestDelegate next)
{
    public async Task InvokeAsync(HttpContext context)
    {
        if (context.GetEndpoint()?.Metadata.GetMetadata<PhotoUploadMetadata>() is null)
        {
            await next(context);
            return;
        }
        // Reject missing CSRF headers before antiforgery can parse an unbounded multipart body.
        if (!context.Request.Headers.ContainsKey("X-CSRF-TOKEN"))
        {
            await Results.Problem(statusCode: 400, title: "Antiforgery validation failed.").ExecuteAsync(context);
            return;
        }
        if (context.Request.ContentLength > ItemPhotoEndpoints.MaximumRequestBytes)
        {
            await TooLarge(context);
            return;
        }
        var limits = context.Features.Get<IHttpMaxRequestBodySizeFeature>();
        if (limits is { IsReadOnly: false })
            limits.MaxRequestBodySize = ItemPhotoEndpoints.MaximumRequestBytes;
        // Buffering remains memory-only below the hard total limit, including chunked requests.
        context.Request.EnableBuffering((int)ItemPhotoEndpoints.MaximumRequestBytes + 1, ItemPhotoEndpoints.MaximumRequestBytes);
        context.Features.Set<IFormFeature>(new FormFeature(context.Request, new FormOptions
        {
            MemoryBufferThreshold = (int)ItemPhotoEndpoints.MaximumRequestBytes + 1,
            MultipartBodyLengthLimit = PhotoProcessor.MaximumBytes,
            ValueCountLimit = 3,
            ValueLengthLimit = 128,
            KeyLengthLimit = 32,
            MultipartHeadersLengthLimit = 4096,
            MultipartBoundaryLengthLimit = 128,
        }));
        try { await next(context); }
        catch (InvalidDataException) { await TooLarge(context); }
        catch (BadHttpRequestException error) when (error.StatusCode == 413) { await TooLarge(context); }
    }

    private static Task TooLarge(HttpContext context) => Results.Problem(statusCode: 413,
        title: "Upload one prepared photo of 4 MiB or less.").ExecuteAsync(context);
}
