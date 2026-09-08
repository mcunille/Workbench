// Copyright (c) 2026 The White Stag Collection.

using System.Diagnostics;
using ImageMagick;
using ImageMagick.Configuration;

namespace Workbench.Server.Inventory;

public sealed class PhotoProcessor
{
    public const int MaximumBytes = 4 * 1024 * 1024;

    private static readonly SemaphoreSlim Capacity = new(1, 1);

    static PhotoProcessor()
    {
        var configuration = ConfigurationFiles.Default;
        configuration.Policy.Data = """
            <policymap>
              <policy domain="delegate" rights="none" pattern="*" />
              <policy domain="filter" rights="none" pattern="*" />
              <policy domain="module" rights="none" pattern="*" />
              <policy domain="module" rights="read|write" pattern="{JPEG,PNG,WEBP}" />
              <policy domain="coder" rights="none" pattern="*" />
              <policy domain="coder" rights="read|write" pattern="{JPEG,PNG,WEBP}" />
              <policy domain="path" rights="none" pattern="*" />
              <policy domain="resource" name="memory" value="256MiB" />
              <policy domain="resource" name="map" value="0" />
              <policy domain="resource" name="disk" value="0" />
              <policy domain="resource" name="thread" value="2" />
              <policy domain="resource" name="width" value="2048" />
              <policy domain="resource" name="height" value="2048" />
              <policy domain="resource" name="list-length" value="2" />
            </policymap>
            """;
        MagickNET.Initialize(configuration);
        ResourceLimits.Memory = 256 * 1024 * 1024;
        ResourceLimits.MaxMemoryRequest = 256 * 1024 * 1024;
        ResourceLimits.MaxProfileSize = MaximumBytes;
        ResourceLimits.Disk = 0;
        ResourceLimits.Thread = 2;
        ResourceLimits.Width = 2048;
        ResourceLimits.Height = 2048;
        ResourceLimits.ListLength = 2;
    }

    public ProcessedPhoto Process(byte[] content)
    {
        if (content.Length > MaximumBytes)
            throw new PhotoInputException(413, "The prepared photograph exceeds 4 MiB.");
        var format = DetectFormat(content);
        CheckDimensions(content, format);
        if (!Capacity.Wait(0))
            throw new PhotoInputException(503, "Photograph processing is busy. Try again shortly.");
        try
        {
            var timer = Stopwatch.StartNew();
            using var image = new MagickImage();
            var warning = false;
            image.Warning += (_, _) => warning = true;
            // Native progress callbacks are cooperative, not a hard execution deadline.
            image.Progress += (_, args) => args.Cancel = timer.Elapsed > TimeSpan.FromSeconds(10);
            image.Read(content, new MagickReadSettings { Format = format });
            if (warning)
                throw new PhotoInputException(422, "The photograph is damaged or has an invalid color profile.");
            if (image.Width > 2048 || image.Height > 2048)
                throw new PhotoInputException(413, "Prepare the photograph at no more than 2048 pixels per edge.");
            image.AutoOrient();
            var profile = image.GetColorProfile();
            if (profile is not null)
            {
                if (!image.TransformColorSpace(ColorProfiles.SRGB))
                    throw new PhotoInputException(422, "The photograph has an invalid color profile.");
            }
            else if (image.ColorSpace == ColorSpace.CMYK)
                throw new PhotoInputException(422, "CMYK photographs require a valid color profile.");
            else
                image.ColorSpace = ColorSpace.sRGB;
            if (warning || timer.Elapsed > TimeSpan.FromSeconds(10))
                throw new PhotoInputException(422, "The photograph could not be processed.");
            image.Strip();
            image.Quality = 90;
            var detail = image.ToByteArray(MagickFormat.WebP);
            using var thumbnail = image.Clone();
            thumbnail.Resize(new MagickGeometry(384, 384) { Greater = true });
            var small = thumbnail.ToByteArray(MagickFormat.WebP);
            if (warning || timer.Elapsed > TimeSpan.FromSeconds(10))
                throw new PhotoInputException(422, "The photograph could not be processed.");
            if (detail.Length + small.Length > 10 * 1024 * 1024)
                throw new PhotoInputException(413, "The processed photograph exceeds the storage limit.");
            return new ProcessedPhoto(detail, small, (int)image.Width, (int)image.Height);
        }
        catch (MagickResourceLimitErrorException)
        {
            throw new PhotoInputException(413, "The photograph exceeds image processing limits.");
        }
        catch (MagickException)
        {
            throw new PhotoInputException(422, "The photograph is damaged or cannot be decoded.");
        }
        finally
        {
            Capacity.Release();
        }
    }

    private static MagickFormat DetectFormat(ReadOnlySpan<byte> bytes)
    {
        if (bytes.StartsWith(new byte[] { 255, 216, 255 }))
        {
            RejectMultipleJpegPictures(bytes);
            return MagickFormat.Jpeg;
        }
        if (bytes.StartsWith(new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 }))
        {
            // APNG's animation control chunk must precede its image data.
            for (var offset = 8; offset + 12 <= bytes.Length;)
            {
                var length = System.Buffers.Binary.BinaryPrimitives.ReadUInt32BigEndian(bytes[offset..]);
                if (bytes.Slice(offset + 4, 4).SequenceEqual("acTL"u8))
                    throw new PhotoInputException(415, "Animated photographs are not supported.");
                if (length > (uint)(bytes.Length - offset - 12)) break;
                offset += (int)length + 12;
            }
            return MagickFormat.Png;
        }
        if (bytes.Length >= 12 && bytes[..4].SequenceEqual("RIFF"u8) && bytes.Slice(8, 4).SequenceEqual("WEBP"u8))
        {
            for (var offset = 12; offset + 8 <= bytes.Length;)
            {
                var length = System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(bytes[(offset + 4)..]);
                if (bytes.Slice(offset, 4).SequenceEqual("ANIM"u8) || bytes.Slice(offset, 4).SequenceEqual("ANMF"u8)
                    || (bytes.Slice(offset, 4).SequenceEqual("VP8X"u8) && length >= 1 && offset + 8 < bytes.Length && (bytes[offset + 8] & 2) != 0))
                    throw new PhotoInputException(415, "Animated photographs are not supported.");
                if (length > (uint)(bytes.Length - offset - 8)) break;
                offset += (int)length + 8 + (int)(length & 1);
            }
            return MagickFormat.WebP;
        }
        throw new PhotoInputException(415, "Use a JPEG, PNG, or WebP photograph.");
    }

    private static void RejectMultipleJpegPictures(ReadOnlySpan<byte> bytes)
    {
        // APP2 MPF metadata can follow the frame header. Inspect every segment up to scan data.
        for (var offset = 2; offset + 4 <= bytes.Length;)
        {
            if (bytes[offset++] != 255) break;
            while (offset < bytes.Length && bytes[offset] == 255) offset++;
            if (offset >= bytes.Length) break;
            var marker = bytes[offset++];
            if (marker is 0xd9 or 0xda) break;
            if (marker is 0x01 or >= 0xd0 and <= 0xd7) continue;
            if (offset + 2 > bytes.Length) break;
            var length = System.Buffers.Binary.BinaryPrimitives.ReadUInt16BigEndian(bytes[offset..]);
            if (length < 2 || length > bytes.Length - offset) break;
            if (marker == 0xe2 && length >= 6 && bytes.Slice(offset + 2, 4).SequenceEqual("MPF\0"u8))
                throw new PhotoInputException(415, "Multi-picture photographs are not supported.");
            offset += length;
        }
    }

    private static void CheckDimensions(ReadOnlySpan<byte> bytes, MagickFormat format)
    {
        uint width = 0, height = 0;
        if (format == MagickFormat.Png && bytes.Length >= 24 && bytes.Slice(12, 4).SequenceEqual("IHDR"u8))
        {
            width = System.Buffers.Binary.BinaryPrimitives.ReadUInt32BigEndian(bytes[16..]);
            height = System.Buffers.Binary.BinaryPrimitives.ReadUInt32BigEndian(bytes[20..]);
        }
        else if (format == MagickFormat.Jpeg)
        {
            for (var offset = 2; offset + 4 <= bytes.Length;)
            {
                if (bytes[offset++] != 255) break;
                while (offset < bytes.Length && bytes[offset] == 255) offset++;
                if (offset >= bytes.Length) break;
                var marker = bytes[offset++];
                if (marker is 0xd9 or 0xda) break;
                if (marker is 0x01 or >= 0xd0 and <= 0xd7) continue;
                if (offset + 2 > bytes.Length) break;
                var length = System.Buffers.Binary.BinaryPrimitives.ReadUInt16BigEndian(bytes[offset..]);
                if (length < 2 || length > bytes.Length - offset) break;
                if (marker is >= 0xc0 and <= 0xcf and not 0xc4 and not 0xc8 and not 0xcc && length >= 7)
                {
                    height = System.Buffers.Binary.BinaryPrimitives.ReadUInt16BigEndian(bytes[(offset + 3)..]);
                    width = System.Buffers.Binary.BinaryPrimitives.ReadUInt16BigEndian(bytes[(offset + 5)..]);
                    break;
                }
                offset += length;
            }
        }
        else if (format == MagickFormat.WebP && bytes.Length >= 25)
        {
            if (bytes.Length >= 30 && bytes.Slice(12, 4).SequenceEqual("VP8X"u8))
            {
                width = 1 + (uint)(bytes[24] | bytes[25] << 8 | bytes[26] << 16);
                height = 1 + (uint)(bytes[27] | bytes[28] << 8 | bytes[29] << 16);
            }
            else if (bytes.Length >= 30 && bytes.Slice(12, 4).SequenceEqual("VP8 "u8))
            {
                width = (uint)(System.Buffers.Binary.BinaryPrimitives.ReadUInt16LittleEndian(bytes[26..]) & 0x3fff);
                height = (uint)(System.Buffers.Binary.BinaryPrimitives.ReadUInt16LittleEndian(bytes[28..]) & 0x3fff);
            }
            else if (bytes.Slice(12, 4).SequenceEqual("VP8L"u8))
            {
                var bits = System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(bytes[21..]);
                width = 1 + (bits & 0x3fff);
                height = 1 + ((bits >> 14) & 0x3fff);
            }
        }
        if (width > 2048 || height > 2048)
            throw new PhotoInputException(413, "Prepare the photograph at no more than 2048 pixels per edge.");
    }
}

public sealed record ProcessedPhoto(byte[] Detail, byte[] Thumbnail, int Width, int Height);

public sealed class PhotoInputException(int statusCode, string message, string? code = null) : Exception(message)
{
    public int StatusCode { get; } = statusCode;
    public string? Code { get; } = code;
}
