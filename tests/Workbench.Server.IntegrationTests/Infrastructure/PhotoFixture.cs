// Copyright (c) 2026 The White Stag Collection.

using System.Buffers.Binary;
using System.IO.Compression;
using System.Text;

namespace Workbench.Server.IntegrationTests.Infrastructure;

internal static class PhotoFixture
{
    // Managed construction avoids ImageMagick's deliberately forbidden XC pseudo-coder.
    internal static byte[] Png(uint width = 12, uint height = 8, byte red = 255, byte green = 0, byte blue = 0, byte alpha = 255)
    {
        using var png = new MemoryStream();
        png.Write(new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 });
        var header = new byte[13];
        BinaryPrimitives.WriteUInt32BigEndian(header, width);
        BinaryPrimitives.WriteUInt32BigEndian(header.AsSpan(4), height);
        header[8] = 8;
        header[9] = 6;
        WriteChunk(png, "IHDR", header);
        using var compressed = new MemoryStream();
        using (var encoder = new ZLibStream(compressed, CompressionLevel.Fastest, true))
        {
            var pixel = new byte[] { red, green, blue, alpha };
            for (var row = 0; row < height; row++)
            {
                encoder.WriteByte(0);
                for (var column = 0; column < width; column++)
                {
                    encoder.Write(pixel);
                }
            }
        }
        WriteChunk(png, "IDAT", compressed.ToArray());
        WriteChunk(png, "IEND", []);
        return png.ToArray();
    }

    internal static void WriteChunk(Stream stream, string type, byte[] data)
    {
        var encoded = Encoding.ASCII.GetBytes(type);
        Span<byte> integer = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(integer, (uint)data.Length);
        stream.Write(integer);
        stream.Write(encoded);
        stream.Write(data);
        uint crc = uint.MaxValue;
        foreach (var value in encoded.Concat(data))
        {
            crc ^= value;
            for (var bit = 0; bit < 8; bit++)
            {
                crc = (crc >> 1) ^ ((crc & 1) == 1 ? 0xedb88320u : 0);
            }
        }
        BinaryPrimitives.WriteUInt32BigEndian(integer, ~crc);
        stream.Write(integer);
    }
}
