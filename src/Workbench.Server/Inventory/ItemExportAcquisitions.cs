// Copyright (c) 2026 The White Stag Collection.

using Microsoft.EntityFrameworkCore;
using Workbench.Server.Persistence;

namespace Workbench.Server.Inventory;

internal static class ItemExportAcquisitions
{
    // Caller holds the serializable item snapshot transaction throughout this read.
    internal static async Task<Dictionary<Guid, ExportAcquisition>> CaptureAsync(
        WorkbenchDbContext database, string scope, CancellationToken cancellationToken)
    {
        var rows = await (from item in database.Items.AsNoTracking()
                          where scope == "all" || item.ArchivedAtUtc == null
                          join link in database.AcquisitionItems.AsNoTracking() on item.Id equals link.ItemId
                          join acquisition in database.Acquisitions.AsNoTracking() on link.AcquisitionId equals acquisition.Id
                          orderby item.CreatedAtUtc, item.Id
                          select new
                          {
                              ItemId = item.Id,
                              Acquisition = new ExportAcquisition(acquisition.Id,
                              acquisition.Method, acquisition.Source, acquisition.Year, acquisition.Month,
                              acquisition.Day, acquisition.Notes)
                          })
            .TagWith("H12 acquisition snapshot").Take(ItemExportCsv.MaximumRows + 1).ToListAsync(cancellationToken);
        if (rows.Count > ItemExportCsv.MaximumRows) throw new ItemExportLimitException();
        return rows.ToDictionary(row => row.ItemId, row => row.Acquisition);
    }
}
