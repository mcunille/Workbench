// Copyright (c) 2026 The White Stag Collection.

using System.Text.Json.Serialization;

namespace Workbench.Server.Inventory;

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record CreateItemRequest(Guid CreationRequestId, string? Name, string? Notes, string? Location);
public sealed record ItemDetailResponse(Guid Id, string Name, string? Notes, string? Location, DateTimeOffset CreatedAtUtc,
    string Version, ItemPhotoResponse? Photo);
public sealed record ItemSummaryResponse(Guid Id, string Name, string? Location, DateTimeOffset CreatedAtUtc, ItemPhotoResponse? Photo);
public sealed record ItemPageResponse(IReadOnlyList<ItemSummaryResponse> Items, string? NextCursor);
public sealed record ItemPhotoResponse(Guid Id, string ThumbnailUrl, string DetailUrl, int Width, int Height);
public sealed record ItemPhotoMutationResponse(Guid RequestId, string Version, Guid? PhotoId);
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record RemoveItemPhotoRequest(Guid RequestId, string ExpectedVersion);
public sealed record UploadItemPhotoRequest(IFormFile File, Guid RequestId, string ExpectedVersion);

public static class ItemInput
{
    public static CreateItemRequest Normalize(CreateItemRequest request) => request with
    {
        Name = request.Name?.Trim(),
        Notes = string.IsNullOrWhiteSpace(request.Notes) ? null : request.Notes,
        Location = string.IsNullOrWhiteSpace(request.Location) ? null : request.Location.Trim(),
    };

    public static Dictionary<string, string[]> Validate(CreateItemRequest request)
    {
        var errors = new Dictionary<string, string[]>();
        if (request.CreationRequestId == Guid.Empty)
            errors["creationRequestId"] = ["A nonempty creation request identifier is required."];
        if (string.IsNullOrWhiteSpace(request.Name) || request.Name.Length > 200)
            errors["name"] = ["Enter a name of 1 to 200 characters."];
        if (request.Notes?.Length > 4000)
            errors["notes"] = ["Notes must be 4,000 characters or fewer."];
        if (request.Location?.Length > 200)
            errors["location"] = ["Location must be 200 characters or fewer."];
        return errors;
    }
}
