// Copyright (c) 2026 The White Stag Collection.

using Microsoft.Data.SqlClient;
using Workbench.Server.Persistence;

return await RunAsync(args);

static async Task<int> RunAsync(string[] arguments)
{
    if (arguments is not ["migrate", "--connection-file", var connectionFile, "--expected-database", var expectedDatabase])
    {
        Console.Error.WriteLine(
            "Usage: Workbench.Database migrate --connection-file <path> --expected-database <name>");
        return 2;
    }

    if (!File.Exists(connectionFile) || string.IsNullOrWhiteSpace(expectedDatabase))
    {
        Console.Error.WriteLine("Migration input is missing or invalid.");
        return 2;
    }

    var connectionString = (await File.ReadAllTextAsync(connectionFile)).Trim();
    var builder = new SqlConnectionStringBuilder(connectionString);
    if (!string.Equals(builder.InitialCatalog, expectedDatabase, StringComparison.Ordinal))
    {
        Console.Error.WriteLine("The connection string database does not match --expected-database.");
        return 2;
    }

    await DatabaseMigrator.MigrateAsync(connectionString, CancellationToken.None);
    Console.WriteLine($"Database '{expectedDatabase}' migrated successfully.");
    return 0;
}
