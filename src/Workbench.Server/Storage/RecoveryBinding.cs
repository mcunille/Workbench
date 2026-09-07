// Copyright (c) 2026 The White Stag Collection.

using Workbench.Server.Operations;

namespace Workbench.Server.Storage;

public static class RecoveryBinding
{
    public static string Validate(IConfiguration configuration, RecoveryInventory inventory)
    {
        var source = configuration.GetSection("Recovery:Source");
        var alias = OperationalConfiguration.ProviderAlias(source);
        if (inventory.Rows.Any(row => row.ProviderAlias != alias))
            throw new InvalidOperationException("Original storage configuration does not match the SQL inventory.");
        var original = PhysicalLocation(source);
        var target = PhysicalLocation(configuration);
        if (original == target || original.StartsWith(target + "/", StringComparison.Ordinal) || target.StartsWith(original + "/", StringComparison.Ordinal))
            throw new InvalidOperationException("Recovery requires a physically separate storage destination.");
        return alias;
    }

    public static string PhysicalLocation(IConfiguration configuration)
    {
        switch (configuration["Storage:Provider"])
        {
            case "Azure":
                var uri = new Uri(configuration["Storage:ContainerUri"] ?? throw new InvalidOperationException("Original container is required."));
                if (uri.Scheme != "https" || uri.Query.Length != 0 || uri.Fragment.Length != 0 || uri.UserInfo.Length != 0)
                    throw new InvalidOperationException("Invalid container binding.");
                // Require another container even when installation namespaces differ.
                return "Azure:" + uri.AbsoluteUri.TrimEnd('/');
            case "FileSystem":
                var root = Path.GetFullPath(configuration["Storage:Root"] ?? throw new InvalidOperationException("Original root is required."));
                // Existing filesystem stores reject symbolic links through ConfinedDirectory.
                // The original root may be unavailable after a disaster, so no source IO is required here.
                root = Path.TrimEndingDirectorySeparator(root).Replace('\\', '/');
                return "FileSystem:" + (OperatingSystem.IsWindows() ? root.ToUpperInvariant() : root);
            default:
                throw new InvalidOperationException("Original storage provider is required.");
        }
    }
}
