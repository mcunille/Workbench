// Copyright (c) 2026 The White Stag Collection.

using System.Security.Cryptography;

namespace Workbench.Server.Storage;

public static class BlobIntegrity
{
    public static Stream Open(Stream content, BlobContentIdentity expected) => new VerifiedStream(content, expected);

    // Streaming consumers must observe successful EOF before treating the transfer as complete.
    private sealed class VerifiedStream(Stream content, BlobContentIdentity expected) : Stream
    {
        private readonly IncrementalHash _hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        private long _length;
        private bool _complete;
        private bool _failed;

        private int Verify(ReadOnlySpan<byte> bytes, int requested)
        {
            if (_failed) { throw new InvalidDataException("Blob integrity verification failed."); }
            if (_complete || requested == 0) { return bytes.Length; }
            _length = checked(_length + bytes.Length);
            _hash.AppendData(bytes);
            if (_length > expected.Length || bytes.Length == 0 &&
                (_length != expected.Length || !Convert.ToHexString(_hash.GetHashAndReset()).Equals(expected.Sha256, StringComparison.OrdinalIgnoreCase)))
            {
                _failed = true;
                throw new InvalidDataException("Blob integrity verification failed.");
            }
            _complete = bytes.Length == 0;
            return bytes.Length;
        }

        public override int Read(byte[] buffer, int offset, int count) => Read(buffer.AsSpan(offset, count));
        public override int Read(Span<byte> buffer)
        {
            var count = content.Read(buffer);
            return Verify(buffer[..count], buffer.Length);
        }
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            var count = await content.ReadAsync(buffer, cancellationToken);
            return Verify(buffer.Span[..count], buffer.Length);
        }
        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
            ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();
        protected override void Dispose(bool disposing)
        {
            if (disposing) { content.Dispose(); _hash.Dispose(); }
            base.Dispose(disposing);
        }
        public override bool CanRead => content.CanRead;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
