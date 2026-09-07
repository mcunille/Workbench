// Copyright (c) 2026 The White Stag Collection.

using System.Net;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Workbench.Server.Identity;
using Workbench.Server.IntegrationTests.Infrastructure;
using Workbench.Server.Operations;
using Workbench.Server.Storage;
using Workbench.Server.Tenancy;
using Xunit;

namespace Workbench.Server.IntegrationTests;

[Collection(SqlServerCollection.Name)]
public sealed class GraphWorkRetryTests(SqlServerFixture sqlServer)
{
    [Theory]
    [InlineData(true, 120.1, 0, 0, 110, 125)]
    [InlineData(true, 7200, 0, 0, 3590, 3605)]
    [InlineData(true, -1, 0, 0, -10, 10)]
    [InlineData(true, 0, 0, 0, -10, 10)]
    [InlineData(false, 120, 0, 3, -10, 10)]
    [InlineData(true, 120, 4, 3, 110, 125)]
    public async Task GraphFailuresUseBoundedDurableRetries(bool transient, double delaySeconds,
        int previousAttempts, int expectedState, int minimumDelay, int maximumDelay)
    {
        // GIVEN a committed encrypted recovery message and a failing Graph provider.
        await using var application = await AuthTestApplication.CreateAsync(sqlServer);
        var delivery = new FailedDelivery(new GraphDeliveryException(transient, TimeSpan.FromSeconds(delaySeconds)));
        await using var factory = application.Factory.WithWebHostBuilder(builder =>
        {
            builder.UseSetting("Identity:PublicRecoveryEnabled", "true");
            builder.ConfigureServices(services =>
            {
                services.RemoveAll<IIdentityMessageDelivery>();
                services.AddSingleton<IIdentityMessageDelivery>(delivery);
            });
        });
        using var client = factory.CreateClient();
        var response = await RecoveryTests.PostWithAntiforgeryAsync(client, "/api/auth/recovery",
            new { email = AuthTestApplication.AdminEmail });
        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        await using var connection = new SqlConnection(application.AdminConnectionString);
        await connection.OpenAsync();
        await using (var attempts = new SqlCommand("UPDATE [Operations].[WorkItems] SET [Attempts] = @attempts", connection))
        {
            attempts.Parameters.AddWithValue("@attempts", previousAttempts);
            await attempts.ExecuteNonQueryAsync();
        }
        var processor = new WorkProcessor(await application.CreateWorkerConnectionAsync(),
            factory.Services.GetRequiredService<TenantContextProof>(),
            factory.Services.GetRequiredService<IDataProtectionProvider>(), delivery,
            new Dictionary<string, IBlobStore>());

        // WHEN the worker attempts delivery once, THEN retry eligibility and delay are durable.
        Assert.True(await processor.RunOnceAsync(CancellationToken.None));
        await using var read = new SqlCommand("""
            SELECT [State], [Attempts], [ProtectedPayload], [Outcome],
                DATEDIFF(second, SYSUTCDATETIME(), [AvailableAtUtc]), [LeaseOwner], [LeaseExpiresAtUtc]
            FROM [Operations].[WorkItems];
            """, connection);
        await using var reader = await read.ExecuteReaderAsync();
        Assert.True(await reader.ReadAsync());
        Assert.Equal(expectedState, reader.GetInt32(0));
        Assert.Equal(previousAttempts + 1, reader.GetInt32(1));
        Assert.Equal(expectedState == 3, reader.IsDBNull(2));
        Assert.Equal(transient ? "ProviderUnavailable" : "Rejected", reader.GetString(3));
        Assert.InRange(reader.GetInt32(4), minimumDelay, maximumDelay);
        Assert.True(reader.IsDBNull(5));
        Assert.True(reader.IsDBNull(6));
        Assert.False(await reader.ReadAsync());
    }

    private sealed class FailedDelivery(Exception error) : IIdentityMessageDelivery
    {
        public bool IsAvailable => true;
        public Task DeliverAsync(IdentityMessage message, CancellationToken cancellationToken) => Task.FromException(error);
    }
}
