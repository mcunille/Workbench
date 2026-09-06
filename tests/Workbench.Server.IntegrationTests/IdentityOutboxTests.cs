// Copyright (c) 2026 The White Stag Collection.

using System.Net;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Workbench.Server.Identity;
using Workbench.Server.IntegrationTests.Infrastructure;
using Xunit;
using Workbench.Server.Operations;
using Workbench.Server.Tenancy;

namespace Workbench.Server.IntegrationTests;

[Collection(SqlServerCollection.Name)]
public sealed class IdentityOutboxTests(SqlServerFixture sqlServer)
{
    [Fact]
    public async Task ExplicitlyDisabledRecoveryRemainsUnavailableWithAWorkingProvider()
    {
        // GIVEN an available development provider with recovery explicitly disabled.
        await using var application = await AuthTestApplication.CreateAsync(sqlServer);
        await using var factory = application.Factory.WithWebHostBuilder(builder =>
            builder.UseSetting("Identity:PublicRecoveryEnabled", "false"));
        using var client = factory.CreateClient();
        // WHEN recovery is requested, THEN the feature flag denies the operation.
        var response = await RecoveryTests.PostWithAntiforgeryAsync(client, "/api/auth/recovery", new { email = AuthTestApplication.AdminEmail });
        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
    }

    [Fact]
    public async Task RecoveryCommitsAnEncryptedOutboxWithoutContactingSmtp()
    {
        // GIVEN SMTP delivery whose server is intentionally unavailable.
        await using var application = await AuthTestApplication.CreateAsync(sqlServer);
        await using var factory = application.Factory.WithWebHostBuilder(builder =>
        {
            builder.UseSetting("Identity:PublicRecoveryEnabled", "true");
            builder.ConfigureServices(services =>
            {
                services.RemoveAll<IIdentityMessageDelivery>();
                services.AddSingleton<IIdentityMessageDelivery>(new SmtpIdentityMessageDelivery(new SmtpOptions
                {
                    Host = "127.0.0.1",
                    Port = 1,
                    Username = "test",
                    Password = "test-password",
                    Sender = "sender@example.com",
                    PublicOrigin = "https://workbench.example",
                }));
            });
        });
        using var client = factory.CreateClient();
        // WHEN account recovery is requested, THEN the request succeeds without network delivery.
        var response = await RecoveryTests.PostWithAntiforgeryAsync(client, "/api/auth/recovery", new { email = AuthTestApplication.AdminEmail });
        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        await using var connection = new SqlConnection(application.AdminConnectionString);
        await connection.OpenAsync();
        await using var read = new SqlCommand("""
            SELECT w.[Id], w.[ProtectedPayload], o.[TokenHash] FROM [Operations].[WorkItems] w
            JOIN [Identity].[IdentityOperations] o ON o.[Id] = w.[IdentityOperationId] AND o.[TenantId] = w.[TenantId]
            WHERE w.[Kind] = 2
            """, connection);
        await using var reader = await read.ExecuteReaderAsync();
        Assert.True(await reader.ReadAsync());
        var job = reader.GetGuid(0);
        var ciphertext = (byte[])reader.GetValue(1);
        // AND the purpose-bound protected payload contains the same single-use token as the operation hash.
        var protector = factory.Services.GetRequiredService<IDataProtectionProvider>()
            .CreateProtector("Workbench.IdentityOutbox.v1", AuthTestApplication.TenantId.ToString("N"), job.ToString("N"));
        var message = System.Text.Json.JsonSerializer.Deserialize<IdentityMessage>(protector.Unprotect(ciphertext))!;
        Assert.Equal((byte[])reader.GetValue(2), SessionToken.Hash(message.Token));
        Assert.DoesNotContain(message.Token, System.Text.Encoding.UTF8.GetString(ciphertext), StringComparison.Ordinal);
        Assert.False(await reader.ReadAsync());
        await reader.DisposeAsync();
        // WHEN a separate worker delivers the durable message.
        var capture = new DevelopmentIdentityMessageDelivery();
        var processor = new WorkProcessor(await application.CreateWorkerConnectionAsync(),
            factory.Services.GetRequiredService<TenantContextProof>(),
            factory.Services.GetRequiredService<IDataProtectionProvider>(), capture,
            new Dictionary<string, Workbench.Server.Storage.IBlobStore>());
        Assert.True(await processor.RunOnceAsync(CancellationToken.None));
        // THEN the same token is delivered once and ciphertext is removed from completed work.
        Assert.Equal(message.Token, Assert.Single(capture.Messages).Token);
        Assert.False(await processor.RunOnceAsync(CancellationToken.None));
        await using var completed = new SqlCommand("SELECT COUNT(*) FROM [Operations].[WorkItems] WHERE [State] = 2 AND [ProtectedPayload] IS NULL", connection);
        Assert.Equal(1, Convert.ToInt32(await completed.ExecuteScalarAsync()));
        // AND restore sanitation removes delivery capabilities without violating outbox references.
        var commands = new Workbench.Server.Administration.OperatorCommands(application.AdminConnectionString,
            new Microsoft.AspNetCore.Identity.PasswordHasher<WorkbenchUser>(), TimeProvider.System);
        await commands.SanitizeRestoreAsync("outbox-restore-test", CancellationToken.None);
        await using var restored = new SqlCommand("SELECT COUNT(*) FROM [Operations].[WorkItems] WHERE [Kind] = 2", connection);
        Assert.Equal(0, Convert.ToInt32(await restored.ExecuteScalarAsync()));
    }
}
