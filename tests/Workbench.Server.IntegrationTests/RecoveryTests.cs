// Copyright (c) 2026 The White Stag Collection.

using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.DependencyInjection;
using Workbench.Server.Identity;
using Workbench.Server.IntegrationTests.Infrastructure;
using Xunit;

namespace Workbench.Server.IntegrationTests;

[Collection(SqlServerCollection.Name)]
public sealed class RecoveryTests(SqlServerFixture sqlServer) : IAsyncLifetime
{
    private AuthTestApplication _application = null!;

    public async Task InitializeAsync() =>
        _application = await AuthTestApplication.CreateAsync(sqlServer);

    public Task DisposeAsync() => _application.DisposeAsync().AsTask();

    [Fact]
    public async Task RecoveryRequestIsNonEnumeratingAndStoresOnlyHash()
    {
        using var knownClient = _application.CreateClient();
        using var unknownClient = _application.CreateClient();
        var known = await PostWithAntiforgeryAsync(
            knownClient,
            "/api/auth/recovery",
            new { email = AuthTestApplication.AdminEmail });
        var unknown = await PostWithAntiforgeryAsync(
            unknownClient,
            "/api/auth/recovery",
            new { email = "unknown@example.com" });

        Assert.Equal(HttpStatusCode.Accepted, known.StatusCode);
        Assert.Equal(HttpStatusCode.Accepted, unknown.StatusCode);
        Assert.Equal(await known.Content.ReadAsStringAsync(), await unknown.Content.ReadAsStringAsync());
        var capture = _application.Factory.Services.GetRequiredService<DevelopmentIdentityMessageDelivery>();
        var token = Assert.Single(capture.Messages).Token;

        await using var connection = new SqlConnection(_application.AdminConnectionString);
        await connection.OpenAsync();
        await using var command = new SqlCommand(
            "SELECT COUNT(*) FROM [Identity].[IdentityOperations] WHERE [TokenHash] = @hash",
            connection);
        command.Parameters.AddWithValue("@hash", SessionToken.Hash(token));
        Assert.Equal(1, Convert.ToInt32(await command.ExecuteScalarAsync()));
    }

    [Fact]
    public async Task ConcurrentRecoveryConsumptionHasExactlyOneWinnerAndRevokesSessions()
    {
        using var signedInClient = _application.CreateClient();
        Assert.Equal(HttpStatusCode.NoContent, (await PostWithAntiforgeryAsync(
            signedInClient,
            "/api/auth/login",
            new { email = AuthTestApplication.AdminEmail, password = AuthTestApplication.AdminPassword }))
            .StatusCode);
        using var requestClient = _application.CreateClient();
        await PostWithAntiforgeryAsync(
            requestClient,
            "/api/auth/recovery",
            new { email = AuthTestApplication.AdminEmail });
        var token = Assert.Single(_application.Factory.Services
            .GetRequiredService<DevelopmentIdentityMessageDelivery>().Messages).Token;
        using var first = _application.CreateClient();
        using var second = _application.CreateClient();

        var results = await Task.WhenAll(
            ConsumeAsync(first, token),
            ConsumeAsync(second, token));

        Assert.Equal(1, results.Count(response => response.StatusCode == HttpStatusCode.NoContent));
        Assert.Equal(1, results.Count(response => response.StatusCode == HttpStatusCode.BadRequest));
        Assert.Equal(HttpStatusCode.Unauthorized, (await signedInClient.GetAsync("/api/auth/me")).StatusCode);
    }

    private static Task<HttpResponseMessage> ConsumeAsync(HttpClient client, string token) =>
        PostWithAntiforgeryAsync(
            client,
            "/api/auth/recovery/consume",
            new { token, newPassword = "Recovered Correct Horse 3#" });

    internal static async Task<HttpResponseMessage> PostWithAntiforgeryAsync(
        HttpClient client,
        string path,
        object body)
    {
        var tokens = await client.GetFromJsonAsync<JsonElement>("/api/auth/antiforgery");
        using var request = new HttpRequestMessage(HttpMethod.Post, path)
        {
            Content = JsonContent.Create(body),
        };
        request.Headers.Add("X-CSRF-TOKEN", tokens.GetProperty("requestToken").GetString());
        return await client.SendAsync(request);
    }
}
