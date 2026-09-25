// Copyright (c) 2026 The White Stag Collection.

using Workbench.Server.Identity;
using Workbench.Server.Tenancy;
using Xunit;

namespace Workbench.Server.IntegrationTests;

public sealed class SessionTokenValidationTests
{
    [Fact]
    public void SessionTokensRequireTheExactEncodedEntropyLength()
    {
        // GIVEN a valid base64url encoding with one byte too much entropy.
        var oversizedToken = Microsoft.AspNetCore.WebUtilities.WebEncoders.Base64UrlEncode(new byte[33]);

        // WHEN validating the token THEN its length is rejected without any application or SQL fixture.
        Assert.False(SessionToken.TryHash(oversizedToken, out _));
    }

    [Fact]
    public async Task ResolveTreatsMalformedTokenAsUnauthenticated()
    {
        // GIVEN a malformed token and deliberately unusable SQL configuration.
        var sessions = new SessionService("not-a-database-connection", new SessionOptions(), new TenantContextProof(new byte[32]));
        // WHEN resolving the token THEN it is unauthenticated before even constructing a SQL connection.
        Assert.Null(await sessions.ResolveAsync("not a base64url token!",
            new DateTimeOffset(2026, 9, 1, 12, 0, 0, TimeSpan.Zero), CancellationToken.None));
    }
}
