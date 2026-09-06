// Copyright (c) 2026 The White Stag Collection.

using System.Buffers;
using System.Security.Cryptography;

namespace Workbench.Server.Storage;

public static class BlobTransfer
{
    public static async Task<BlobContentIdentity> CopyAsync(Stream source, Stream destination, long maximumBytes,
        CancellationToken cancellationToken)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maximumBytes);
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var buffer = ArrayPool<byte>.Shared.Rent(64 * 1024);
        long length = 0;
        try
        {
            while (true)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var read = await source.ReadAsync(buffer.AsMemory(0, 64 * 1024), cancellationToken);
                if (read == 0)
                {
                    break;
                }
                if (read > maximumBytes - length)
                {
                    throw new InvalidDataException("Blob content exceeds its configured limit.");
                }
                await destination.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
                hash.AppendData(buffer, 0, read);
                length += read;
            }
            return new BlobContentIdentity(length, Convert.ToHexString(hash.GetHashAndReset()));
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer, clearArray: true);
        }
    }
}
