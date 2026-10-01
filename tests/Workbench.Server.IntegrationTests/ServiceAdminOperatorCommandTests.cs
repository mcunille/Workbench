// Copyright (c) 2026 The White Stag Collection.

using System.Diagnostics;
using System.Net;
using System.Net.Http.Json;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text.Json;
using Microsoft.Data.SqlClient;
using Workbench.Server.IntegrationTests.Infrastructure;
using Xunit;

namespace Workbench.Server.IntegrationTests;

[Collection(SqlServerCollection.Name)]
public sealed class ServiceAdminOperatorCommandTests(SqlServerFixture sqlServer) : IAsyncLifetime
{
    private const string Email = "operator-admin@example.com";
    private const string Password = "CLI Sentinel Password 72!";
    private const string ReplacementPassword = "CLI Replacement Password 83!";
    private const string Auth = "/api/beta/service-admin/auth";
    private AuthTestApplication _application = null!;
    private string _operatorConnection = null!;
    private string _directory = null!;
    private string _connectionFile = null!;
    private string _passwordFile = null!;
    private string _expectedDatabase = null!;

    public async Task InitializeAsync()
    {
        _application = await AuthTestApplication.CreateAsync(sqlServer);
        _operatorConnection = await _application.CreateOperatorConnectionAsync();
        _expectedDatabase = new SqlConnectionStringBuilder(_operatorConnection).InitialCatalog;
        _directory = Path.Combine(Path.GetTempPath(), "workbench-admin-command-" + Guid.NewGuid().ToString("N"));
        if (OperatingSystem.IsWindows())
        {
            var security = new DirectorySecurity();
            security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
            security.AddAccessRule(new FileSystemAccessRule(WindowsIdentity.GetCurrent().User!, FileSystemRights.FullControl,
                InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit, PropagationFlags.None, AccessControlType.Allow));
            new DirectoryInfo(_directory).Create(security);
        }
        else
        {
            Directory.CreateDirectory(_directory, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }
        _connectionFile = Path.Combine(_directory, "connection.txt");
        _passwordFile = Path.Combine(_directory, "password.txt");
        await File.WriteAllTextAsync(_connectionFile, _operatorConnection);
        await File.WriteAllTextAsync(_passwordFile, Password + "\r\n");
    }

    public async Task DisposeAsync()
    {
        try { if (_directory is not null && Directory.Exists(_directory)) Directory.Delete(_directory, recursive: true); }
        finally { if (_application is not null) await _application.DisposeAsync(); }
    }

    [Fact]
    public async Task OperatorLifecycleChangesAuthorityOnTheNextHttpRequest()
    {
        // GIVEN an installation operator and secret files, without a service-admin identity
        var provision = await RunAsync("provision", "--email", "  OPERATOR-admin@example.com  ", "--password-file", _passwordFile);
        AssertSuccess(provision);
        var accountId = ReadAccountId(provision.Output);
        using var client = _application.CreateClient();
        Assert.Equal(HttpStatusCode.NoContent, await LoginAsync(client, Password));
        var me = await client.GetFromJsonAsync<JsonElement>(Auth + "/me");
        Assert.Equal(accountId, me.GetProperty("accountId").GetGuid());
        Assert.Equal("OPERATOR-admin@example.com", me.GetProperty("email").GetString());

        // WHEN all service-admin sessions are revoked THEN the old browser loses authority immediately
        AssertOutcome(await RunAsync("revoke-sessions", "--account-id", accountId.ToString()), "Service-admin sessions revoked successfully.");
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync(Auth + "/me")).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, await LoginAsync(client, Password));

        // WHEN the lost credential is reset THEN both the old session and old password are rejected
        await File.WriteAllTextAsync(_passwordFile, ReplacementPassword);
        AssertOutcome(await RunAsync("reset-password", "--account-id", accountId.ToString(), "--password-file", _passwordFile),
            "Service-admin password reset successfully.");
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync(Auth + "/me")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, await LoginAsync(client, Password));
        Assert.Equal(HttpStatusCode.NoContent, await LoginAsync(client, ReplacementPassword));

        // WHEN the identity is disabled THEN its existing browser and subsequent sign-in are denied
        AssertOutcome(await RunAsync("disable", "--account-id", accountId.ToString()), "Service-admin account disabled successfully.");
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync(Auth + "/me")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, await LoginAsync(client, ReplacementPassword));
    }

    [Fact]
    public async Task ResettingADisabledAccountDoesNotEnableIt()
    {
        // GIVEN an identity disabled through the operator command
        var provision = await RunAsync("provision", "--email", Email, "--password-file", _passwordFile);
        AssertSuccess(provision);
        var id = ReadAccountId(provision.Output).ToString();
        AssertSuccess(await RunAsync("disable", "--account-id", id));
        // WHEN the password is reset THEN the replacement still cannot sign in
        await File.WriteAllTextAsync(_passwordFile, ReplacementPassword);
        AssertSuccess(await RunAsync("reset-password", "--account-id", id, "--password-file", _passwordFile));
        using var client = _application.CreateClient();
        Assert.Equal(HttpStatusCode.Unauthorized, await LoginAsync(client, ReplacementPassword));
    }

    [Fact]
    public async Task DuplicateProvisioningFailsWithoutCreatingAnotherAccount()
    {
        // GIVEN an existing normalized email provisioned by the real CLI
        AssertSuccess(await RunAsync("provision", "--email", Email, "--password-file", _passwordFile));
        var before = await StateAsync();
        // WHEN the same email is provisioned with different casing THEN no partial write occurs
        AssertFailure(await RunAsync("provision", "--email", Email.ToUpperInvariant(), "--password-file", _passwordFile));
        Assert.Equal(before, await StateAsync());
    }

    [Theory]
    [InlineData("invalid-email")]
    [InlineData("empty-email")]
    [InlineData("oversized-email")]
    [InlineData("weak-password")]
    [InlineData("oversized-password")]
    [InlineData("weak-reset-password")]
    [InlineData("oversized-reset-password")]
    [InlineData("invalid-id")]
    [InlineData("empty-id")]
    [InlineData("absent-target")]
    [InlineData("absent-reset-target")]
    [InlineData("absent-revoke-target")]
    [InlineData("missing-password-file")]
    [InlineData("missing-connection-file")]
    [InlineData("wrong-database")]
    [InlineData("unknown-option")]
    [InlineData("dangling-option")]
    [InlineData("duplicate-option")]
    [InlineData("unexpected-option")]
    [InlineData("unknown-action")]
    [InlineData("missing-required-option")]
    public async Task RejectedInputsProduceSafeFailureWithoutChangingAccountState(string scenario)
    {
        // GIVEN existing identity state and protected command inputs
        await _application.ProvisionServiceAdminAsync();
        var before = await StateAsync();
        string[] options = ["--email", Email, "--password-file", _passwordFile];
        var action = "provision";
        var connectionFile = _connectionFile;
        var expectedDatabase = _expectedDatabase;
        switch (scenario)
        {
            case "invalid-email": options[1] = "no-at-sign"; break;
            case "empty-email": options[1] = " "; break;
            case "oversized-email": options[1] = new string('a', 245) + "@example.com"; break;
            case "weak-password": await File.WriteAllTextAsync(_passwordFile, "short"); break;
            case "oversized-password": await File.WriteAllTextAsync(_passwordFile, new string('a', 1025) + "1!"); break;
            case "weak-reset-password":
            case "oversized-reset-password":
                action = "reset-password";
                options = ["--account-id", AuthTestApplication.ServiceAdminId.ToString(), "--password-file", _passwordFile];
                await File.WriteAllTextAsync(_passwordFile, scenario == "weak-reset-password" ? "short" : new string('a', 1025) + "1!");
                break;
            case "invalid-id": action = "disable"; options = ["--account-id", "invalid"]; break;
            case "empty-id": action = "disable"; options = ["--account-id", Guid.Empty.ToString()]; break;
            case "absent-target": action = "disable"; options = ["--account-id", Guid.NewGuid().ToString()]; break;
            case "absent-reset-target": action = "reset-password"; options = ["--account-id", Guid.NewGuid().ToString(), "--password-file", _passwordFile]; break;
            case "absent-revoke-target": action = "revoke-sessions"; options = ["--account-id", Guid.NewGuid().ToString()]; break;
            case "missing-password-file": options[3] += ".missing"; break;
            case "missing-connection-file": connectionFile += ".missing"; break;
            case "wrong-database": expectedDatabase += "_wrong"; break;
            case "unknown-option": options = [.. options, "--tenant-id", AuthTestApplication.TenantId.ToString()]; break;
            case "dangling-option": options = [.. options, "--email"]; break;
            case "duplicate-option": options = [.. options, "--email", "second@example.com"]; break;
            case "unexpected-option": action = "disable"; options = ["--account-id", AuthTestApplication.ServiceAdminId.ToString(), "--password-file", _passwordFile]; break;
            case "unknown-action": action = "enable"; break;
            case "missing-required-option": options = ["--email", Email]; break;
        }
        // WHEN the command rejects malformed, unsupported or misdirected input
        AssertFailure(await RunAsync(action, options, connectionFile, expectedDatabase));
        // THEN existing identity authority and credentials remain intact
        Assert.Equal(before, await StateAsync());
    }

    [Theory]
    [InlineData("web", "provision")]
    [InlineData("web", "disable")]
    [InlineData("web", "reset-password")]
    [InlineData("web", "revoke-sessions")]
    [InlineData("worker", "provision")]
    [InlineData("worker", "disable")]
    [InlineData("worker", "reset-password")]
    [InlineData("worker", "revoke-sessions")]
    public async Task RuntimeCredentialsCannotUseOperatorMaintenance(string role, string action)
    {
        // GIVEN the same CLI input backed by an actual restricted workload principal
        await _application.ProvisionServiceAdminAsync();
        var before = await StateAsync();
        var restricted = role == "web" ? _application.WebConnectionString : await _application.CreateWorkerConnectionAsync();
        await using (var connection = new SqlConnection(restricted))
        {
            // AND the supplied workload credential connects successfully, so login failure cannot satisfy the denial
            await connection.OpenAsync();
            await using var probe = new SqlCommand("SELECT 1", connection);
            Assert.Equal(1, await probe.ExecuteScalarAsync());
        }
        await File.WriteAllTextAsync(_connectionFile, restricted);
        string[] options = action == "provision" ? ["--email", Email] : ["--account-id", AuthTestApplication.ServiceAdminId.ToString()];
        if (action is "provision" or "reset-password") options = [.. options, "--password-file", _passwordFile];
        // WHEN invoking maintenance THEN the actual supplied principal is denied with no credential disclosure
        var result = await RunAsync(action, options);
        AssertFailure(result);
        Assert.Equal(before, await StateAsync());
    }

    private Task<Result> RunAsync(string action, params string[] options) => RunAsync(action, options, _connectionFile, _expectedDatabase);

    private async Task<Result> RunAsync(string action, string[] options, string connectionFile, string expectedDatabase)
    {
        var testOutput = new DirectoryInfo(AppContext.BaseDirectory);
        var root = testOutput;
        while (root is not null && !File.Exists(Path.Combine(root.FullName, "Workbench.slnx"))) root = root.Parent;
        Assert.NotNull(root);
        var assembly = Path.Combine(root.FullName, "src", "Workbench.Database", "bin", testOutput.Parent!.Name, testOutput.Name, "Workbench.Database.dll");
        Assert.True(File.Exists(assembly), "Database CLI must be built from current source by the project reference.");
        var start = new ProcessStartInfo("dotnet") { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true, WorkingDirectory = _directory };
        // Explicit environment prevents ambient production/development connections from becoming fallback inputs.
        var path = Environment.GetEnvironmentVariable("PATH");
        var systemRoot = Environment.GetEnvironmentVariable("SystemRoot");
        var dotnetRoot = Environment.GetEnvironmentVariable("DOTNET_ROOT");
        start.Environment.Clear();
        start.Environment["PATH"] = path;
        if (systemRoot is not null) start.Environment["SystemRoot"] = systemRoot;
        if (dotnetRoot is not null) start.Environment["DOTNET_ROOT"] = dotnetRoot;
        start.Environment["DOTNET_CLI_HOME"] = _directory;
        start.Environment["DOTNET_NOLOGO"] = "1";
        start.Environment["DOTNET_SKIP_FIRST_TIME_EXPERIENCE"] = "1";
        foreach (var argument in new[] { assembly, "service-admin", action, "--connection-file", connectionFile, "--expected-database", expectedDatabase }.Concat(options)) start.ArgumentList.Add(argument);
        using var process = Process.Start(start)!;
        var stdout = process.StandardOutput.ReadToEndAsync();
        var stderr = process.StandardError.ReadToEndAsync();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        try { await process.WaitForExitAsync(timeout.Token); }
        catch { process.Kill(entireProcessTree: true); throw; }
        var result = new Result(process.ExitCode, await stdout, await stderr);
        var output = result.Output + result.Error;
        Assert.False(output.Contains(Password, StringComparison.Ordinal) || output.Contains(ReplacementPassword, StringComparison.Ordinal) ||
            output.Contains(_operatorConnection, StringComparison.Ordinal), "CLI disclosed protected command inputs.");
        if (File.Exists(connectionFile))
        {
            var suppliedConnection = (await File.ReadAllTextAsync(connectionFile)).Trim();
            var sqlPassword = new SqlConnectionStringBuilder(suppliedConnection).Password;
            Assert.False(output.Contains(suppliedConnection, StringComparison.Ordinal) || output.Contains(sqlPassword, StringComparison.Ordinal),
                "CLI disclosed supplied SQL credentials.");
        }
        return result;
    }

    private static void AssertSuccess(Result result) { Assert.Equal(0, result.ExitCode); Assert.True(result.Error.Length == 0, "Successful CLI output included an error."); }
    private static void AssertFailure(Result result)
    {
        Assert.Equal(1, result.ExitCode);
        Assert.True(result.Output.Length == 0, "Failed CLI command emitted an unexpected result.");
        Assert.True(result.Error.Trim() == "Database command failed. No credentials were printed.", "Failed CLI command did not return the generic error.");
    }
    private static void AssertOutcome(Result result, string outcome) { AssertSuccess(result); Assert.True(outcome == result.Output.Trim(), "CLI output included more than the action outcome."); }
    private static Guid ReadAccountId(string output)
    {
        var id = Guid.Parse(output.Trim().Split(' ').Last().TrimEnd('.'));
        Assert.True($"Service-admin account provisioned successfully: {id}." == output.Trim(), "CLI provision output included more than the account outcome and ID.");
        return id;
    }

    private static async Task<HttpStatusCode> LoginAsync(HttpClient client, string password)
    {
        var csrf = await client.GetFromJsonAsync<JsonElement>(Auth + "/antiforgery");
        using var request = new HttpRequestMessage(HttpMethod.Post, Auth + "/login") { Content = JsonContent.Create(new { email = Email, password }) };
        request.Headers.Add("X-CSRF-TOKEN", csrf.GetProperty("requestToken").GetString());
        using var response = await client.SendAsync(request);
        return response.StatusCode;
    }

    private async Task<string> StateAsync()
    {
        await using var connection = new SqlConnection(_application.AdminConnectionString);
        await connection.OpenAsync();
        await using var command = new SqlCommand("SELECT Id,Email,NormalizedEmail,PasswordHash,IsEnabled,SecurityVersion FROM ServiceAdministration.Accounts ORDER BY Id FOR JSON PATH", connection);
        var state = (string)(await command.ExecuteScalarAsync())!;
        return Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(state)));
    }

    private sealed record Result(int ExitCode, string Output, string Error);
}
