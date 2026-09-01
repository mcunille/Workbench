// Copyright (c) 2026 The White Stag Collection.

using System.Net;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using Xunit;

namespace Workbench.Server.IntegrationTests;

public sealed class SystemEndpointTests
{
    [Fact]
    public async Task GetSystemReturnsTypedApplicationIdentity()
    {
        await using var factory = new WebApplicationFactory<Program>();
        using var client = factory.CreateClient();

        var response = await client.GetAsync("/api/system");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var body = await response.Content.ReadFromJsonAsync<SystemPayload>();
        Assert.Equal("Workbench", body?.Name);
        Assert.False(string.IsNullOrWhiteSpace(body?.Version));
    }

    private sealed record SystemPayload(string Name, string Version);
}
