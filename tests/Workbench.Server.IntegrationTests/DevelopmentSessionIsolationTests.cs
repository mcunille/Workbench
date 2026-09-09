// Copyright (c) 2026 The White Stag Collection.

using System.Security.Cryptography;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Workbench.Server.Identity;
using Workbench.Server.Security;
using Xunit;

namespace Workbench.Server.IntegrationTests;

public sealed class DevelopmentSessionIsolationTests
{
    [Theory]
    [InlineData(null, "")]
    [InlineData("preview-a1", ".preview-a1")]
    [InlineData("0", ".0")]
    [InlineData("abcdefghijklmnopqrstuvwxyz0123456789-abcdefghijklmnopqrstuvwxyz0", ".abcdefghijklmnopqrstuvwxyz0123456789-abcdefghijklmnopqrstuvwxyz0")]
    public async Task DevelopmentNamesUseTheOptionalEnvironmentSuffix(string? id, string suffix)
    {
        // GIVEN an explicitly configured development host without a database.
        await using var factory = CreateFactory("Development", id);
        // WHEN resolving the registered authentication, antiforgery and protection options.
        var session = factory.Services.GetRequiredService<IOptionsMonitor<CookieAuthenticationOptions>>()
            .Get(SessionCookieHandler.Scheme);
        var antiforgery = factory.Services.GetRequiredService<IOptions<AntiforgeryOptions>>().Value;
        var protection = factory.Services.GetRequiredService<IOptions<DataProtectionOptions>>().Value;
        // THEN all browser state namespaces use the same suffix while security settings remain intact.
        Assert.Equal(".Workbench.Session" + suffix, session.Cookie.Name);
        Assert.Equal(".Workbench.Antiforgery" + suffix, antiforgery.Cookie.Name);
        Assert.Equal("Workbench" + suffix, protection.ApplicationDiscriminator);
        Assert.True(session.Cookie.HttpOnly);
        Assert.True(antiforgery.Cookie.HttpOnly);
        Assert.Equal(SameSiteMode.Lax, session.Cookie.SameSite);
        Assert.Equal(SameSiteMode.Strict, antiforgery.Cookie.SameSite);
        Assert.Equal(CookieSecurePolicy.SameAsRequest, session.Cookie.SecurePolicy);
        Assert.Equal(CookieSecurePolicy.SameAsRequest, antiforgery.Cookie.SecurePolicy);
    }

    [Theory]
    [InlineData("")]
    [InlineData(" ")]
    [InlineData("UPPER")]
    [InlineData("-preview")]
    [InlineData("preview; path=/")]
    [InlineData("preview_a")]
    [InlineData("préview")]
    [InlineData("preview\n")]
    [InlineData("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa")]
    public async Task InvalidDevelopmentIdentityFailsStartup(string id)
    {
        // GIVEN an invalid explicit environment ID.
        await using var factory = CreateFactory("Development", id);
        // WHEN starting the host THEN reject it before accepting requests without echoing input.
        var error = Assert.Throws<InvalidOperationException>(() => factory.CreateClient());
        Assert.Equal("Development:EnvironmentId must contain 1 to 64 lowercase ASCII letters, digits or hyphens and start with a letter or digit.", error.Message);
    }

    [Theory]
    [InlineData("Production")]
    [InlineData("Staging")]
    public async Task NonDevelopmentHostsIgnoreTheDevelopmentSetting(string environment)
    {
        // GIVEN a non-development host with an invalid development-only setting.
        await using var factory = CreateFactory(environment, "invalid; setting");
        // WHEN resolving its browser security configuration.
        var session = factory.Services.GetRequiredService<IOptionsMonitor<CookieAuthenticationOptions>>()
            .Get(SessionCookieHandler.Scheme);
        var antiforgery = factory.Services.GetRequiredService<IOptions<AntiforgeryOptions>>().Value;
        // THEN production cookie names, protection identity and transport requirements are unchanged.
        Assert.Equal("__Host-Workbench.Session", session.Cookie.Name);
        Assert.Equal("__Host-Workbench.Antiforgery", antiforgery.Cookie.Name);
        Assert.Equal("Workbench", factory.Services.GetRequiredService<IOptions<DataProtectionOptions>>().Value.ApplicationDiscriminator);
        Assert.Equal(CookieSecurePolicy.Always, session.Cookie.SecurePolicy);
        Assert.Equal(CookieSecurePolicy.Always, antiforgery.Cookie.SecurePolicy);
    }

    [Fact]
    public async Task DifferentEnvironmentsCannotUnprotectEachOthersPayloadsEvenWithSharedKeys()
    {
        // GIVEN development hosts sharing physical key storage, as a defence against accidental reuse.
        var directory = Directory.CreateTempSubdirectory("workbench-isolation-");
        try
        {
            await using var first = CreateFactory("Development", "first")
                .WithWebHostBuilder(builder => builder.ConfigureServices(services =>
                    services.AddDataProtection().PersistKeysToFileSystem(directory)));
            await using var second = CreateFactory("Development", "second")
                .WithWebHostBuilder(builder => builder.ConfigureServices(services =>
                    services.AddDataProtection().PersistKeysToFileSystem(directory)));
            var firstProtector = first.Services.GetRequiredService<IDataProtectionProvider>().CreateProtector("test");
            var secondProtector = second.Services.GetRequiredService<IDataProtectionProvider>().CreateProtector("test");
            // WHEN the first environment protects a payload.
            var protectedValue = firstProtector.Protect("test payload");
            // THEN it can read its own payload but the other environment cannot.
            Assert.Equal("test payload", firstProtector.Unprotect(protectedValue));
            Assert.Throws<CryptographicException>(() => secondProtector.Unprotect(protectedValue));
        }
        finally
        {
            directory.Delete(recursive: true);
        }
    }
    private static WebApplicationFactory<Program> CreateFactory(string environment, string? id) =>
        new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.UseEnvironment(environment);
            if (id is not null) builder.UseSetting("Development:EnvironmentId", id);
            builder.UseSetting("ConnectionStrings:Workbench", "");
            builder.UseSetting("Storage:Provider", "Azure");
            builder.UseSetting("Storage:ContainerUri", "https://test.blob.core.windows.net/blobs");
            builder.UseSetting("Storage:InstallationId", "aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa");
            builder.ConfigureServices(services =>
            {
                // No live infrastructure is involved; production validator coverage remains in its own suite.
                var validator = services.Single(descriptor => descriptor.ImplementationType == typeof(ProductionSecurityConfigurationValidator));
                services.Remove(validator);
            });
        });
}
