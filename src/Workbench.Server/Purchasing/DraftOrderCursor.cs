// Copyright (c) 2026 The White Stag Collection.

using System.Globalization;

namespace Workbench.Server.Purchasing;

public static class DraftOrderCursor
{
    public static bool TryDecode(string? cursor, out DateTimeOffset timestamp, out Guid id)
    {
        timestamp = default;
        id = default;
        if (cursor is null) return true;
        if (cursor.Length > 128) return false;
        var parts = cursor.Split('_');
        return parts.Length == 3 && parts[0] == "v1" && DateTimeOffset.TryParseExact(parts[1], "O", CultureInfo.InvariantCulture,
            DateTimeStyles.None, out timestamp) && timestamp.Offset == TimeSpan.Zero &&
            Guid.TryParseExact(parts[2], "N", out id) && id != Guid.Empty;
    }

    public static string Encode(DateTimeOffset timestamp, Guid id) => $"v1_{timestamp.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture)}_{id:N}";
    public static string Timestamp(DateTimeOffset timestamp) => timestamp.UtcDateTime.ToString("O", CultureInfo.InvariantCulture);
}
