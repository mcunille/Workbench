// Copyright (c) 2026 The White Stag Collection.

using Microsoft.AspNetCore.Identity;
using Workbench.Server.Administration;
using Workbench.Server.Identity;
using Xunit;

namespace Workbench.Server.IntegrationTests;

public sealed class BootstrapValidationTests
{
    [Theory]
    [InlineData("alllowercasepassword")]
    [InlineData("No-Symbols-Or-Digits")]
    [InlineData("Short-4!")]
    public async Task BootstrapRejectsPasswordsOutsideTheApplicationPolicy(string password)
    {
        // GIVEN an invalid administrator password and deliberately unusable SQL configuration.
        var commands = new OperatorCommands(
            "not-a-database-connection",
            new PasswordHasher<WorkbenchUser>(),
            TimeProvider.System);

        // WHEN bootstrap is requested THEN password validation rejects it before constructing SQL.
        var error = await Assert.ThrowsAsync<ArgumentException>(() => commands.BootstrapAsync(
            "First Tenant",
            "admin@example.com",
            password,
            CancellationToken.None));
        // AND an ArgumentException from parsing the connection string cannot satisfy this contract.
        Assert.Equal("administratorPassword", error.ParamName);
        Assert.Equal(
            "The password does not satisfy the Workbench password policy. (Parameter 'administratorPassword')",
            error.Message);
    }
}
