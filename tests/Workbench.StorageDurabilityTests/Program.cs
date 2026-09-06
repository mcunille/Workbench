// Copyright (c) 2026 The White Stag Collection.

using Workbench.Server.Storage;

var store = new FileSystemBlobStore(args[0]);
var id = new BlobObjectId(Guid.Parse("79289486-b55b-43d4-9dd7-259ff3c4a634"), Guid.Parse("54289486-b55b-43d4-9dd7-259ff3c4a634"));
try
{
    switch (args[1])
    {
        case "stage":
            store.StageAsync(id, new MemoryStream([1, 2, 3]), 3, CancellationToken.None).GetAwaiter().GetResult();
            break;
        case "publish":
            store.PublishAsync(id, CancellationToken.None).GetAwaiter().GetResult();
            break;
        case "delete":
            store.DeleteAsync(id, CancellationToken.None).GetAwaiter().GetResult();
            break;
        case "copy-existing":
            var entry = new BlobManifestEntry(id.TenantId, id.RevisionId, store.Alias, 3,
                Convert.ToHexString(System.Security.Cryptography.SHA256.HashData([1, 2, 3])));
            BlobMaintenance.CopyAsync(store, store, entry, CancellationToken.None).GetAwaiter().GetResult();
            break;
        default:
            throw new ArgumentException("Unknown probe action.");
    }
    Console.WriteLine("ACK");
    return 0;
}
catch (IOException)
{
    Console.WriteLine("IO_FAILURE");
    return 42;
}
