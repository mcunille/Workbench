// Copyright (c) 2026 The White Stag Collection.

using System.Globalization;
using System.Text;

namespace Workbench.Server.Inventory;

public sealed record ExportItemsRequest(string? Scope);

public sealed record ExportItem(Guid Id, string TrackingKind, string Name, string? Notes,
    string? Location, DateTimeOffset CreatedAtUtc, DateTimeOffset? ArchivedAtUtc, ExportAcquisition? Acquisition = null);

public sealed record ExportAcquisition(Guid Id, string Method, string? Source, int? Year,
    int? Month, int? Day, string? Notes)
{
    public string DatePrecision => Day.HasValue ? "day" : Month.HasValue ? "month" : Year.HasValue ? "year" : "unknown";
}

public sealed class ItemExportLimitException : Exception;

public static class ItemExportCsv
{
    public const int MaximumRows = 10_000;
    public const int MaximumBytes = 32 * 1024 * 1024;
    public const string Header = "schema_version,exported_at_utc,scope,item_id,tracking_kind,name,notes,location,is_archived,created_at_utc,archived_at_utc,acquisition_id,acquisition_method,acquisition_source,acquisition_date_precision,acquisition_year,acquisition_month,acquisition_day,acquisition_notes";

    public static byte[] Encode(IReadOnlyList<ExportItem> items, string scope, DateTimeOffset exportedAt,
        CancellationToken cancellationToken)
    {
        if (items.Count > MaximumRows)
            throw new ItemExportLimitException();
        using var output = new MemoryStream();
        output.Write(Encoding.UTF8.GetPreamble());
        WriteRow(Header.Split(','));
        foreach (var item in items)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var acquisition = item.Acquisition;
            WriteRow(["2", Timestamp(exportedAt), scope, item.Id.ToString("D"), item.TrackingKind,
                SafeText(item.Name), SafeText(item.Notes), SafeText(item.Location),
                item.ArchivedAtUtc.HasValue ? "true" : "false", Timestamp(item.CreatedAtUtc),
                item.ArchivedAtUtc is { } archivedAt ? Timestamp(archivedAt) : "",
                acquisition?.Id.ToString("D") ?? "", acquisition?.Method ?? "", SafeText(acquisition?.Source),
                acquisition?.DatePrecision ?? "", acquisition?.Year?.ToString(CultureInfo.InvariantCulture) ?? "",
                acquisition?.Month?.ToString(CultureInfo.InvariantCulture) ?? "", acquisition?.Day?.ToString(CultureInfo.InvariantCulture) ?? "",
                SafeText(acquisition?.Notes)]);
        }
        return output.ToArray();

        void WriteRow(string[] fields)
        {
            // Each row is bounded by the durable item field lengths. Never assemble the whole CSV as text.
            var line = string.Join(',', fields.Select(field => "\"" + field.Replace("\"", "\"\"", StringComparison.Ordinal) + "\"")) + "\r\n";
            var bytes = Encoding.UTF8.GetBytes(line);
            if (output.Length + bytes.Length > MaximumBytes)
                throw new ItemExportLimitException();
            output.Write(bytes);
        }
    }

    private static string SafeText(string? value) => value is null ? "" : "'" + value;
    private static string Timestamp(DateTimeOffset value) => value.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss.fffffff'Z'", CultureInfo.InvariantCulture);
}
