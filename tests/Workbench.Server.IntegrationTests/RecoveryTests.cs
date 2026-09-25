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
            "/api/beta/auth/recovery",
            new { email = AuthTestApplication.AdminEmail });
        var unknown = await PostWithAntiforgeryAsync(
            unknownClient,
            "/api/beta/auth/recovery",
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
            "/api/beta/auth/login",
            new { email = AuthTestApplication.AdminEmail, password = AuthTestApplication.AdminPassword }))
            .StatusCode);
        using var requestClient = _application.CreateClient();
        await PostWithAntiforgeryAsync(
            requestClient,
            "/api/beta/auth/recovery",
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
        Assert.Equal(HttpStatusCode.Unauthorized, (await signedInClient.GetAsync("/api/beta/auth/me")).StatusCode);
    }

    [Fact]
    public async Task RecoveryConsumptionRejectsOversizedPasswordBeforeChangingTheAccount()
    {
        const string newPassword = "Recovered Correct Horse 3#";
        using var signedInClient = _application.CreateClient();
        using var requestClient = _application.CreateClient();
        using var originalCredentialsClient = _application.CreateClient();
        using var oldCredentialsClient = _application.CreateClient();
        using var newCredentialsClient = _application.CreateClient();

        // GIVEN an existing authenticated session and an unused recovery token.
        Assert.Equal(HttpStatusCode.NoContent, (await PostWithAntiforgeryAsync(
            signedInClient,
            "/api/beta/auth/login",
            new { email = AuthTestApplication.AdminEmail, password = AuthTestApplication.AdminPassword })).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await signedInClient.GetAsync("/api/beta/auth/me")).StatusCode);
        await PostWithAntiforgeryAsync(
            requestClient,
            "/api/beta/auth/recovery",
            new { email = AuthTestApplication.AdminEmail });
        var token = Assert.Single(_application.Factory.Services
            .GetRequiredService<DevelopmentIdentityMessageDelivery>().Messages).Token;

        // WHEN an oversized replacement password is submitted.
        var response = await PostWithAntiforgeryAsync(
            requestClient,
            "/api/beta/auth/recovery/consume",
            new { token, newPassword = $"Aa1!{new string('x', WorkbenchPasswordPolicy.MaximumLength)}" });

        // THEN the request is rejected without changing the credential or revoking the session.
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await PostWithAntiforgeryAsync(
            originalCredentialsClient,
            "/api/beta/auth/login",
            new { email = AuthTestApplication.AdminEmail, password = AuthTestApplication.AdminPassword })).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await signedInClient.GetAsync("/api/beta/auth/me")).StatusCode);

        // WHEN the same token is consumed with a valid password.
        var consumed = await PostWithAntiforgeryAsync(
            requestClient,
            "/api/beta/auth/recovery/consume",
            new { token, newPassword });

        // THEN the credential changes and the pre-existing session is revoked.
        Assert.Equal(HttpStatusCode.NoContent, consumed.StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await signedInClient.GetAsync("/api/beta/auth/me")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await PostWithAntiforgeryAsync(
            oldCredentialsClient,
            "/api/beta/auth/login",
            new { email = AuthTestApplication.AdminEmail, password = AuthTestApplication.AdminPassword })).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await PostWithAntiforgeryAsync(
            newCredentialsClient,
            "/api/beta/auth/login",
            new { email = AuthTestApplication.AdminEmail, password = newPassword })).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await newCredentialsClient.GetAsync("/api/beta/auth/me")).StatusCode);
    }

    [Theory]
    [InlineData("/api/beta/auth/recovery/consume", true)]
    [InlineData("/api/beta/auth/recovery/consume", false)]
    [InlineData("/api/beta/auth/invitations/consume", true)]
    [InlineData("/api/beta/auth/invitations/consume", false)]
    public async Task IdentityOperationConsumptionRejectsNullCredentials(string path, bool nullToken)
    {
        using var client = _application.CreateClient();
        var response = await PostWithAntiforgeryAsync(
            client,
            path,
            nullToken
                ? new RecoveryConsumeRequest(null!, "Valid Password 1!")
                : new RecoveryConsumeRequest(SessionToken.Create(), null!));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    private static Task<HttpResponseMessage> ConsumeAsync(HttpClient client, string token) =>
        PostWithAntiforgeryAsync(
            client,
            "/api/beta/auth/recovery/consume",
            new { token, newPassword = "Recovered Correct Horse 3#" });

    internal static async Task<HttpResponseMessage> PostWithAntiforgeryAsync(
        HttpClient client,
        string path,
        object body)
    {
        var tokens = await client.GetFromJsonAsync<JsonElement>("/api/beta/auth/antiforgery");
        using var request = new HttpRequestMessage(HttpMethod.Post, path)
        {
            Content = JsonContent.Create(body),
        };
        request.Headers.Add("X-CSRF-TOKEN", tokens.GetProperty("requestToken").GetString());
        return await client.SendAsync(request);
    }
}
