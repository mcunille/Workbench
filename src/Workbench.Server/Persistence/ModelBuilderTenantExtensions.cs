// Copyright (c) 2026 The White Stag Collection.

using System.Linq.Expressions;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Workbench.Server.Tenancy;

namespace Workbench.Server.Persistence;

public static class ModelBuilderTenantExtensions
{
    public static EntityTypeBuilder<TEntity> IsTenantOwned<TEntity>(
        this EntityTypeBuilder<TEntity> entity,
        Expression<Func<TEntity, bool>> queryFilter)
        where TEntity : class, ITenantOwned
    {
        entity.Property(row => row.TenantId).IsRequired();
        entity.HasAlternateKey(nameof(ITenantOwned.TenantId), "Id");
        entity.HasQueryFilter("TenantFilter", queryFilter);
        return entity;
    }
}
