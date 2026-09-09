// Copyright (c) 2026 The White Stag Collection.

namespace Workbench.Server.Inventory;

// A seekable ZIP target whose writes (including disposal/central-directory writes) are bounded.
internal sealed class PackageBuffer(int maximumBytes) : MemoryStream
{
    private void RequireSpace(int count)
    {
        if (count > maximumBytes - Position) throw new ItemExportLimitException();
        Reserve(Position + count);
    }

    private void Reserve(long length)
    {
        // MemoryStream's default doubling can allocate beyond the content ceiling.
        if (length > Capacity)
            Capacity = (int)Math.Min(maximumBytes, Math.Max(length, Math.Max(256, 2L * Capacity)));
    }

    public override void Write(byte[] buffer, int offset, int count)
    {
        RequireSpace(count);
        base.Write(buffer, offset, count);
    }

    public override void Write(ReadOnlySpan<byte> buffer)
    {
        RequireSpace(buffer.Length);
        base.Write(buffer);
    }

    public override void WriteByte(byte value)
    {
        RequireSpace(1);
        base.WriteByte(value);
    }

    public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Write(buffer, offset, count);
        return Task.CompletedTask;
    }

    public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Write(buffer.Span);
        return ValueTask.CompletedTask;
    }

    public override void SetLength(long value)
    {
        if (value > maximumBytes) throw new ItemExportLimitException();
        Reserve(value);
        base.SetLength(value);
    }
}
