// Copyright (c) 2026 The White Stag Collection.
using System.Security.Cryptography;
using Workbench.Server.Administration;
using Xunit;

namespace Workbench.Server.IntegrationTests;

public sealed class PasswordPrincipalProvisioningValidationTests
{
    [Theory]
    [InlineData("missing")]
    [InlineData("empty")]
    [InlineData("null-user")]
    [InlineData("invalid-name")]
    public async Task MalformedPrincipalIdentityRejectsBeforeReadingCredentials(string invalid)
    {
        // GIVEN otherwise distinct principals whose credential files must never be read.
        PasswordPrincipal[] definitions =
        [new("web_user", "unavailable-input", "workbench_web"),
         new("operator_user", "unavailable-input", "workbench_operator"),
         new("migrator_user", "unavailable-input", "workbench_migrator")];
        definitions[0] = invalid switch
        {
            "missing" => null!,
            "empty" => definitions[0] with { User = "" },
            "null-user" => definitions[0] with { User = null! },
            _ => definitions[0] with { User = "invalid user" }
        };

        // WHEN provisioning malformed identity metadata THEN validation precedes any file/SQL access.
        await Assert.ThrowsAsync<ArgumentException>(() =>
            PasswordPrincipalProvisioning.ProvisionAsync("", definitions, RandomNumberGenerator.GetBytes(32)));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(15)]
    public async Task ShortCredentialRejectsBeforeOpeningDatabase(int length)
    {
        // GIVEN distinct valid principals and a privately generated credential below the minimum length.
        var path = Path.GetTempFileName();
        try
        {
            await File.WriteAllTextAsync(path, Convert.ToHexString(RandomNumberGenerator.GetBytes(16))[..length]);
            PasswordPrincipal[] definitions =
            [new("web_user", path, "workbench_web"), new("operator_user", path, "workbench_operator"),
             new("migrator_user", path, "workbench_migrator")];

            // WHEN provisioning THEN the credential guard rejects before an empty SQL connection is opened.
            await Assert.ThrowsAsync<ArgumentException>(() =>
                PasswordPrincipalProvisioning.ProvisionAsync("", definitions, RandomNumberGenerator.GetBytes(32)));
        }
        finally { File.Delete(path); }
    }
}
