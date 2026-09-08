// Copyright (c) 2026 The White Stag Collection.

using System.Reflection;
using Workbench.Server.IntegrationTests.Infrastructure;
using Xunit;

namespace Workbench.Server.IntegrationTests;

public sealed class TestCollectionTests
{
    [Fact]
    public void PhotoOnlyRunsDoNotStartSqlOrCompeteWithOtherPhotoProcessing()
    {
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
