// Copyright (c) 2026 The White Stag Collection.

using Microsoft.Data.SqlClient;
using Microsoft.AspNetCore.Identity;
using Workbench.Server.Administration;
using Workbench.Server.Identity;
using Workbench.Server.Persistence;
using Workbench.Server.Storage;

return await RunAsync(args);

static async Task<int> RunAsync(string[] arguments)
{
    try
    {
        var hasSubcommand = arguments.Length > 1 &&
            arguments[0] is "tenant" or "principals" or "restore" or "development" or "storage";
        var options = ParseOptions(arguments.Skip(hasSubcommand ? 2 : 1));
        if (arguments.Length == 0 ||
            !options.TryGetValue("--connection-file", out var connectionFile) ||
            !options.TryGetValue("--expected-database", out var expectedDatabase))
        {
            return Usage();
        }

        var connectionString = await ReadValidatedConnectionAsync(connectionFile, expectedDatabase);
        if (arguments is ["storage", var action, ..])
        {
            await StorageMaintenanceCommand.RunAsync(action, connectionString, expectedDatabase, options, CancellationToken.None);
            Console.WriteLine("Storage maintenance completed.");
            return 0;
        }
        if (arguments[0] == "migrate")
        {
            await DatabaseMigrator.MigrateAsync(connectionString, CancellationToken.None);
            Console.WriteLine($"Database '{expectedDatabase}' migrated successfully.");
            return 0;
        }

        if (arguments is ["principals", "provision", ..])
        {
            await ProvisionPrincipalsAsync(connectionString, options);
            Console.WriteLine("Workbench database principals provisioned successfully.");
            return 0;
        }

        if (arguments is ["restore", "sanitize", ..])
        {
            var correlationId = RequireOption(options, "--correlation-id");
            var restoreCommands = new OperatorCommands(
                connectionString,
                new PasswordHasher<WorkbenchUser>(),
                TimeProvider.System);
            await restoreCommands.SanitizeRestoreAsync(correlationId, CancellationToken.None);
            Console.WriteLine("Restored authentication artifacts sanitized successfully.");
            return 0;
        }

        if (arguments is ["development", "recovery-link", ..])
        {
            if (!string.Equals(RequireOption(options, "--environment"), "Development", StringComparison.Ordinal) ||
                !Uri.TryCreate(RequireOption(options, "--base-url"), UriKind.Absolute, out var baseUri))
            {
                throw new ArgumentException("Development recovery input is invalid.");
            }
            var email = RequireOption(options, "--email");
            var outputFile = Path.GetFullPath(RequireOption(options, "--output-file"));
            if (File.Exists(outputFile))
            {
                throw new ArgumentException("The recovery output file already exists.");
            }
            var developmentCommands = new OperatorCommands(
                connectionString,
                new PasswordHasher<WorkbenchUser>(),
                TimeProvider.System);
            var token = await developmentCommands.CreateDevelopmentRecoveryAsync(email, CancellationToken.None)
                ?? throw new ArgumentException("The development recovery target was not found.");
            var link = new UriBuilder(new Uri(baseUri, "/recover")) { Query = $"token={Uri.EscapeDataString(token)}" };
            await File.WriteAllTextAsync(outputFile, link.Uri.AbsoluteUri);
            Console.WriteLine("Development recovery link written to the requested file.");
            return 0;
        }

        var bootstrap = arguments[0] == "bootstrap";
        var tenantCreate = arguments is ["tenant", "create", ..];
        if (!bootstrap && !tenantCreate)
        {
            return Usage();
        }

        if (!options.TryGetValue("--tenant-name", out var tenantName) ||
            !options.TryGetValue("--admin-email", out var administratorEmail) ||
            !options.TryGetValue("--password-file", out var passwordFile) ||
            !File.Exists(passwordFile))
        {
            return Usage();
        }

        var password = (await File.ReadAllTextAsync(passwordFile)).TrimEnd('\r', '\n');
        var commands = new OperatorCommands(
            connectionString,
            new PasswordHasher<WorkbenchUser>(),
            TimeProvider.System);
        if (bootstrap)
        {
            await commands.BootstrapAsync(tenantName, administratorEmail, password, CancellationToken.None);
        }
        else
        {
            await commands.CreateAdditionalTenantAsync(
                tenantName,
                administratorEmail,
                password,
                CancellationToken.None);
        }

        Console.WriteLine(bootstrap
            ? "Initial Workbench tenant provisioned successfully."
            : "Additional Workbench tenant provisioned successfully.");
        return 0;
    }
    catch (BootstrapAlreadyCompletedException error)
    {
        Console.Error.WriteLine(error.Message);
        return 3;
    }
    catch (Exception)
    {
        Console.Error.WriteLine("Database command failed. No credentials were printed.");
        return 1;
    }
}

static Dictionary<string, string> ParseOptions(IEnumerable<string> arguments)
{
    var values = arguments.ToArray();
    var options = new Dictionary<string, string>(StringComparer.Ordinal);
    for (var index = 0; index + 1 < values.Length; index += 2)
    {
        options[values[index]] = values[index + 1];
    }

    return options;
}

static async Task<string> ReadValidatedConnectionAsync(string connectionFile, string expectedDatabase)
{
    if (!File.Exists(connectionFile) || string.IsNullOrWhiteSpace(expectedDatabase))
    {
        throw new ArgumentException("Database command input is missing or invalid.");
    }

    var connectionString = (await File.ReadAllTextAsync(connectionFile)).Trim();
    var builder = new SqlConnectionStringBuilder(connectionString);
    if (!string.Equals(builder.InitialCatalog, expectedDatabase, StringComparison.Ordinal))
    {
        throw new ArgumentException("The connection string database does not match --expected-database.");
    }

    return connectionString;
}

static int Usage()
{
    Console.Error.WriteLine("""
        Usage:
          Workbench.Database migrate --connection-file <path> --expected-database <name>
          Workbench.Database bootstrap --connection-file <path> --expected-database <name> --tenant-name <name> --admin-email <email> --password-file <path>
          Workbench.Database tenant create --connection-file <path> --expected-database <name> --tenant-name <name> --admin-email <email> --password-file <path>
          Workbench.Database principals provision --connection-file <path> --expected-database <name> --web-user <name> --web-password-file <path> --operator-user <name> --operator-password-file <path> --migrator-user <name> --migrator-password-file <path> --tenant-context-proof-key-file <path>
          Workbench.Database restore sanitize --connection-file <path> --expected-database <name> --correlation-id <id>
          Workbench.Database storage <manifest|snapshot|verify|migrate|reconcile> --connection-file <maintenance-path> --expected-database <name> --config-file <path> --offline-confirmation "OFFLINE <name>" [--output-file <new-path>] [--manifest-file <path>]
          Workbench.Database development recovery-link --connection-file <path> --expected-database <name> --environment Development --base-url <url> --email <email> --output-file <path>
        """);
    return 2;
}

static async Task ProvisionPrincipalsAsync(
    string connectionString,
    IReadOnlyDictionary<string, string> options)
{
    var definitions = new[]
    {
        (User: RequireOption(options, "--web-user"), PasswordFile: RequireOption(options, "--web-password-file"), Role: "workbench_web"),
        (User: RequireOption(options, "--operator-user"), PasswordFile: RequireOption(options, "--operator-password-file"), Role: "workbench_operator"),
        (User: RequireOption(options, "--migrator-user"), PasswordFile: RequireOption(options, "--migrator-password-file"), Role: "workbench_migrator"),
    };
    await using var connection = new SqlConnection(connectionString);
    await connection.OpenAsync();
    foreach (var definition in definitions)
    {
        if (!System.Text.RegularExpressions.Regex.IsMatch(definition.User, "^[A-Za-z][A-Za-z0-9_]{2,63}$") ||
            !File.Exists(definition.PasswordFile))
        {
            throw new ArgumentException("Database principal input is invalid.");
        }

        var password = (await File.ReadAllTextAsync(definition.PasswordFile)).TrimEnd('\r', '\n');
        if (password.Length < 16)
        {
            throw new ArgumentException("Database principal passwords must contain at least 16 characters.");
        }

        var escapedPassword = password.Replace("'", "''", StringComparison.Ordinal);
        await using var command = new SqlCommand($"""
            IF USER_ID(N'{definition.User}') IS NULL
                CREATE USER [{definition.User}] WITH PASSWORD = N'{escapedPassword}';
            IF NOT EXISTS
            (
                SELECT 1 FROM sys.database_role_members
                WHERE role_principal_id = DATABASE_PRINCIPAL_ID(N'{definition.Role}')
                    AND member_principal_id = DATABASE_PRINCIPAL_ID(N'{definition.User}')
            )
                ALTER ROLE [{definition.Role}] ADD MEMBER [{definition.User}];
            """, connection);
        await command.ExecuteNonQueryAsync();
    }

    var proofKeyFile = RequireOption(options, "--tenant-context-proof-key-file");
    if (!File.Exists(proofKeyFile))
    {
        throw new ArgumentException("The tenant context proof key file does not exist.");
    }

    byte[] proofKey;
    try
    {
        proofKey = Convert.FromBase64String((await File.ReadAllTextAsync(proofKeyFile)).Trim());
    }
    catch (FormatException error)
    {
        throw new ArgumentException("The tenant context proof key must be valid Base64.", error);
    }
    if (proofKey.Length != 32)
    {
        throw new ArgumentException("The tenant context proof key must contain exactly 32 bytes.");
    }

    await using var proofCommand = new SqlCommand(
        "UPDATE [Security].[TenantContextKeys] SET [ProofKey] = @proofKey WHERE [Id] = 1",
        connection);
    proofCommand.Parameters.Add(new SqlParameter("@proofKey", System.Data.SqlDbType.Binary, 32)
    {
        Value = proofKey,
    });
    if (await proofCommand.ExecuteNonQueryAsync() != 1)
    {
        throw new InvalidOperationException("The tenant context proof key store is missing.");
    }
}

static string RequireOption(IReadOnlyDictionary<string, string> options, string name) =>
    options.TryGetValue(name, out var value) && !string.IsNullOrWhiteSpace(value)
        ? value
        : throw new ArgumentException($"Required option '{name}' is missing.");
