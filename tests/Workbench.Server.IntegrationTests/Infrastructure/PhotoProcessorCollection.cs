// Copyright (c) 2026 The White Stag Collection.

using Xunit;

namespace Workbench.Server.IntegrationTests.Infrastructure;

// PhotoProcessor has process-wide native policy and capacity. Exclude other
// collections while these tests run, without starting a SQL container.
[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class PhotoProcessorCollection
{
    public const string Name = "Photo processor";
}
