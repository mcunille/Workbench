// Copyright (c) 2026 The White Stag Collection.

using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;

namespace Workbench.Server.Tenancy;

public sealed class TenantSaveChangesInterceptor(TenantContext tenantContext) : SaveChangesInterceptor
{
    public override InterceptionResult<int> SavingChanges(
        DbContextEventData eventData,
        InterceptionResult<int> result)
    {
        Validate(eventData.Context);
        return result;
    }

    public override ValueTask<InterceptionResult<int>> SavingChangesAsync(
        DbContextEventData eventData,
        InterceptionResult<int> result,
        CancellationToken cancellationToken = default)
    {
        Validate(eventData.Context);
        return ValueTask.FromResult(result);
    }

    private void Validate(DbContext? database)
    {
        if (database is null)
        {
            return;
        }

        foreach (var entry in database.ChangeTracker.Entries<ITenantOwned>()
                     .Where(entry => entry.State is EntityState.Added or EntityState.Modified or EntityState.Deleted))
        {
            var currentTenantId = tenantContext.RequireTenantId();

            if (entry.State is EntityState.Added && entry.Entity.TenantId == Guid.Empty)
            {
                entry.Entity.TenantId = currentTenantId;
            }

            if (entry.Entity.TenantId != currentTenantId ||
                entry.Property(nameof(ITenantOwned.TenantId)).IsModified)
            {
                throw new InvalidOperationException("Tenant ownership cannot be changed or overridden.");
            }
        }
    }
}
