// Copyright (c) 2026 The White Stag Collection.

using Xunit;

namespace Workbench.Server.IntegrationTests.Infrastructure;

[CollectionDefinition(Name)]
public sealed class SqlServerCollection : ICollectionFixture<SqlServerFixture>
{
    public const string Name = "SQL Server";
}
