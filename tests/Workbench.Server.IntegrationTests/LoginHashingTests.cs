// Copyright (c) 2026 The White Stag Collection.

using System.Net;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Workbench.Server.Identity;
using Workbench.Server.IntegrationTests.Infrastructure;
using Xunit;

namespace Workbench.Server.IntegrationTests;

[Collection(SqlServerCollection.Name)]
public sealed class LoginHashingTests(SqlServerFixture sqlServer)
{
    [Theory]
    [InlineData(0, "admin@example.com")]
    [InlineData(1, "missing@example.com")]
    [InlineData(2, "")]
    public async Task RejectedRequestsDoNotPerformPasswordWork(int permits, string email)
    {
        // GIVEN the real verifier registration and a limiter rejecting network/account attempts or invalid input
        await using var application = await AuthTestApplication.CreateAsync(sqlServer);
        var hasher = new CountingHasher();
        await using var factory = application.Factory.WithWebHostBuilder(builder =>
            builder.ConfigureServices(services =>
            {
                services.RemoveAll<IPasswordHasher<WorkbenchUser>>();
                services.AddScoped<IPasswordHasher<WorkbenchUser>>(_ => hasher);
                services.RemoveAll<ISensitiveRequestRateLimiter>();
                services.AddSingleton<ISensitiveRequestRateLimiter>(new LimitedAttempts(permits));
            }));
        using var client = factory.CreateClient();
        var token = await client.GetFromJsonAsync<AntiforgeryResponse>("/api/auth/antiforgery");
        client.DefaultRequestHeaders.Add("X-CSRF-TOKEN", token!.RequestToken);
        var preparedHashes = hasher.HashCalls;

        // WHEN multiple independent request scopes submit rejected login attempts
        for (var attempt = 0; attempt < 3; attempt++)
        {
            using var response = await client.PostAsJsonAsync("/api/auth/login", new { email, password = "wrong" });
            Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        }

        // THEN rejected attempts perform neither hash preparation nor credential comparison
        Assert.Equal(preparedHashes, hasher.HashCalls);
        Assert.Equal(0, hasher.VerificationCalls);
    }

    [Theory]
    [InlineData("missing@example.com", HttpStatusCode.Unauthorized, 1)]
    [InlineData(AuthTestApplication.AdminEmail, HttpStatusCode.NoContent, 0)]
    public async Task AdmittedRequestsComparePasswordsAcrossScopes(string email, HttpStatusCode expected, int hashes)
    {
        // GIVEN admitted login requests using the production verifier and real password hashing
        await using var application = await AuthTestApplication.CreateAsync(sqlServer);
        var hasher = new CountingHasher();
        await using var factory = application.Factory.WithWebHostBuilder(builder =>
            builder.ConfigureServices(services =>
            {
                services.RemoveAll<IPasswordHasher<WorkbenchUser>>();
                services.AddScoped<IPasswordHasher<WorkbenchUser>>(_ => hasher);
                services.RemoveAll<ISensitiveRequestRateLimiter>();
                services.AddSingleton<ISensitiveRequestRateLimiter>(new LimitedAttempts(2));
            }));
        using var client = factory.CreateClient();
        var token = await client.GetFromJsonAsync<AntiforgeryResponse>("/api/auth/antiforgery");
        client.DefaultRequestHeaders.Add("X-CSRF-TOKEN", token!.RequestToken);

        // WHEN separate request scopes concurrently verify credentials
        var responses = await Task.WhenAll(Enumerable.Range(0, 3).Select(_ =>
            client.PostAsJsonAsync("/api/auth/login", new { email, password = AuthTestApplication.AdminPassword })));

        // THEN every admitted request compares a password and missing accounts share one dummy hash
        foreach (var response in responses)
        {
            using (response) { Assert.Equal(expected, response.StatusCode); }
        }
        Assert.Equal(hashes, hasher.HashCalls);
        Assert.Equal(3, hasher.VerificationCalls);
    }

    private sealed class LimitedAttempts(int permits) : ISensitiveRequestRateLimiter
    {
        private int _calls;
        public bool IsAvailable => true;
        public ValueTask<bool> TryAcquireAsync(string partition, CancellationToken cancellationToken) =>
            ValueTask.FromResult(permits == 2 || permits == 1 && Interlocked.Increment(ref _calls) % 2 == 1);
    }

    private sealed class CountingHasher : IPasswordHasher<WorkbenchUser>
    {
        private readonly PasswordHasher<WorkbenchUser> _inner = new();
        private int _hashCalls;
        private int _verificationCalls;
        public int HashCalls => _hashCalls;
        public int VerificationCalls => _verificationCalls;
        public string HashPassword(WorkbenchUser user, string password)
        {
            Interlocked.Increment(ref _hashCalls);
            return _inner.HashPassword(user, password);
        }
        public PasswordVerificationResult VerifyHashedPassword(WorkbenchUser user, string hashedPassword, string providedPassword)
        {
            Interlocked.Increment(ref _verificationCalls);
            return _inner.VerifyHashedPassword(user, hashedPassword, providedPassword);
        }
    }
}
