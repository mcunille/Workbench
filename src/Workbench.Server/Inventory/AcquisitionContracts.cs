// Copyright (c) 2026 The White Stag Collection.

using System.Text.Json.Serialization;

namespace Workbench.Server.Inventory;

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record CreateAcquisitionRequest(Guid CreationRequestId, string? ExpectedItemVersion,
    string? Method, string? Source, int? Year, int? Month, int? Day, string? Notes);

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record UpdateAcquisitionRequest(string? ExpectedItemVersion, string? ExpectedAcquisitionVersion,
    string? Method, string? Source, int? Year, int? Month, int? Day, string? Notes);

public sealed record AcquisitionResponse(Guid Id, string Method, string? Source, int? Year, int? Month,
    int? Day, string? Notes, string Version);
public sealed record ItemAcquisitionResponse(AcquisitionResponse? Acquisition, string ItemVersion);

public static class AcquisitionInput
{
    public static CreateAcquisitionRequest Normalize(CreateAcquisitionRequest request) => request with
    {
        Source = string.IsNullOrWhiteSpace(request.Source) ? null : request.Source.Trim(),
        Notes = string.IsNullOrWhiteSpace(request.Notes) ? null : request.Notes,
    };

    public static Dictionary<string, string[]> Validate(CreateAcquisitionRequest request, TimeProvider timeProvider)
    {
        var errors = new Dictionary<string, string[]>();
        if (request.CreationRequestId == Guid.Empty)
            errors["creationRequestId"] = ["A nonempty creation request identifier is required."];
        if (request.Method is not ("Purchase" or "Gift" or "Inheritance" or "Trade" or "Other" or "Unknown"))
            errors["method"] = ["Choose how you acquired this piece, including Unknown if needed."];
        if (request.Source?.Length > 200)
            errors["source"] = ["Source must be 200 characters or fewer."];
        if (request.Notes?.Length > 4000)
            errors["notes"] = ["Notes must be 4,000 characters or fewer."];
        if (request.Year is < 1 or > 9999)
            errors["year"] = ["Enter a year from 1 to 9999."];
        if (request.Month is not null && (request.Year is null || request.Month is < 1 or > 12))
            errors["month"] = ["A month from 1 to 12 requires a year."];
        if (request.Day is not null && (request.Year is null || request.Month is null || request.Day < 1 ||
            (request.Year is >= 1 and <= 9999 && request.Month is >= 1 and <= 12 && request.Day > DateTime.DaysInMonth(request.Year.Value, request.Month.Value))))
            errors["day"] = ["Enter a valid calendar day with a year and month."];
        if (!errors.ContainsKey("year") && !errors.ContainsKey("month") && !errors.ContainsKey("day") && request.Year is not null)
        {
            var earliest = new DateOnly(request.Year.Value, request.Month ?? 1, request.Day ?? 1);
            if (earliest > DateOnly.FromDateTime(timeProvider.GetUtcNow().UtcDateTime))
                errors[request.Day is not null ? "day" : request.Month is not null ? "month" : "year"] =
                    ["The date acquired cannot be in the future; this is not an expected delivery date."];
        }
        return errors;
    }

    public static byte[] Version(string? input, string field, Dictionary<string, string[]> errors)
    {
        var bytes = new byte[8];
        if (input is null || !Convert.TryFromBase64String(input, bytes, out var written) || written != 8)
            errors[field] = ["A valid saved version is required."];
        return bytes;
    }
}
