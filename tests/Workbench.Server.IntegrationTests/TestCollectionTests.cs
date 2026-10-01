// Copyright (c) 2026 The White Stag Collection.

using System.Reflection;
using Workbench.Server.IntegrationTests.Infrastructure;
using Xunit;

namespace Workbench.Server.IntegrationTests;

public sealed class TestCollectionTests
{
    [Fact]
    public async Task EveryServerTestHasAThirtySecondBudgetWithSupportedRunnerScheduling()
    {
        await Task.Yield();
        // GIVEN every server test, including each independently discovered theory row.
        var assembly = typeof(TestCollectionTests).Assembly;
        var tests = assembly.GetTypes().SelectMany(type => type.GetMethods())
            .Select(method => (method, fact: method.GetCustomAttribute<Xunit.FactAttribute>()))
            .Where(test => test.fact is not null).ToArray();
        // WHEN the runner reads the suite metadata THEN no test can silently omit the time budget.
        Assert.NotEmpty(tests);
        Assert.Empty(tests.Where(test => test.fact!.Timeout != 30_000)
            .Select(test => $"{test.method.DeclaringType!.Name}.{test.method.Name}"));
        // AND timeouts run with the scheduling that xUnit requires for reliable enforcement.
        Assert.True(assembly.GetCustomAttribute<CollectionBehaviorAttribute>()?.DisableTestParallelization);
    }

    [Fact]
    public async Task PhotoOnlyRunsDoNotStartSqlOrCompeteWithOtherPhotoProcessing()
    {
        await Task.Yield();
        // GIVEN the collection used when selecting only PhotoProcessorTests.
        var name = typeof(PhotoProcessorTests).CustomAttributes.Single(attribute =>
            attribute.AttributeType == typeof(CollectionAttribute)).ConstructorArguments[0].Value;
        var definition = typeof(PhotoProcessorTests).Assembly.GetTypes().Single(type =>
            type.CustomAttributes.Any(attribute => attribute.AttributeType == typeof(CollectionDefinitionAttribute) &&
                Equals(attribute.ConstructorArguments[0].Value, name)));
        // WHEN xUnit constructs that collection THEN it has no SQL fixture to start.
        Assert.DoesNotContain(typeof(ICollectionFixture<SqlServerFixture>), definition.GetInterfaces());
        // AND it cannot compete with endpoint tests for the process-wide native processor.
        Assert.True(definition.GetCustomAttribute<CollectionDefinitionAttribute>()!.DisableParallelization);
    }
}
