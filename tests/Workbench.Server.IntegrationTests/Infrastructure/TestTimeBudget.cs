// Copyright (c) 2026 The White Stag Collection.

global using FactAttribute = Workbench.Server.IntegrationTests.Infrastructure.BudgetedFactAttribute;
global using TheoryAttribute = Workbench.Server.IntegrationTests.Infrastructure.BudgetedTheoryAttribute;

// xUnit v2 requires serial collection scheduling for per-case timeouts. The full
// gate still runs independent assembly partitions in separate processes.
// Test methods must be async, including pure tests: yielding once lets the
// runner enforce the budget even if the remaining body is synchronous.
[assembly: Xunit.CollectionBehavior(DisableTestParallelization = true)]

namespace Workbench.Server.IntegrationTests.Infrastructure;

public sealed class BudgetedFactAttribute : Xunit.FactAttribute
{
    public BudgetedFactAttribute() => Timeout = 30_000;
}

public sealed class BudgetedTheoryAttribute : Xunit.TheoryAttribute
{
    public BudgetedTheoryAttribute() => Timeout = 30_000;
}
