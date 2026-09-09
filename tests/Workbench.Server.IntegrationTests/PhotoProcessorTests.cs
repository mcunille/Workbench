// Copyright (c) 2026 The White Stag Collection.

using ImageMagick;
using Workbench.Server.Inventory;
using Workbench.Server.IntegrationTests.Infrastructure;
using Xunit;

namespace Workbench.Server.IntegrationTests;

[Collection(PhotoProcessorCollection.Name)]
public sealed class PhotoProcessorTests
{
    private readonly PhotoProcessor processor = new();

    [Fact]
    public void SanitizesAndCreatesProportionalThumbnail()
    {
        // GIVEN a browser-sized photograph with a private comment.
        using var input = CreateImage(MagickColors.Red, 800, 400);
        input.Comment = "private location";
        var bytes = input.ToByteArray(MagickFormat.Png);
        // WHEN the server processes the image.
        var result = processor.Process(bytes);
        // THEN it emits fresh WebP variants with no private metadata.
        using var detail = new MagickImage(result.Detail);
        using var thumbnail = new MagickImage(result.Thumbnail);
        Assert.Equal(MagickFormat.WebP, detail.Format);
        Assert.Equal(800, result.Width);
        Assert.Equal(400, result.Height);
        Assert.Equal(384u, thumbnail.Width);
        Assert.Equal(192u, thumbnail.Height);
        Assert.True(string.IsNullOrEmpty(detail.Comment));
        Assert.Empty(detail.ProfileNames);
    }

    [Theory]
    [InlineData(MagickFormat.Jpeg)]
    [InlineData(MagickFormat.Png)]
    [InlineData(MagickFormat.WebP)]
    public void SupportedSmallImagesAreNotUpscaled(MagickFormat format)
    {
        // GIVEN a small photograph in each supported raster format.
        using var input = CreateImage(MagickColors.Blue, 32, 16);
        // WHEN processing it.
        var result = processor.Process(input.ToByteArray(format));
        // THEN both variants retain the original dimensions.
        using var thumbnail = new MagickImage(result.Thumbnail);
        Assert.Equal(32, result.Width);
        Assert.Equal(16, result.Height);
        Assert.Equal(32u, thumbnail.Width);
        Assert.Equal(16u, thumbnail.Height);
    }

    [Fact]
    public void AppliesOrientationBeforeRemovingExif()
    {
        // GIVEN an oriented portrait with private EXIF camera information.
        using var input = CreateImage(MagickColors.Red, 80, 40);
        var exif = new ExifProfile();
        exif.SetValue(ExifTag.Orientation, (ushort)6);
        exif.SetValue(ExifTag.Artist, "Private photographer");
        input.SetProfile(exif);
        input.Orientation = OrientationType.RightTop;
        // WHEN processing the JPEG.
        var result = processor.Process(input.ToByteArray(MagickFormat.Jpeg));
        // THEN pixels have the portrait orientation and metadata is absent.
        Assert.Equal(40, result.Width);
        Assert.Equal(80, result.Height);
        using var detail = new MagickImage(result.Detail);
        Assert.Empty(detail.ProfileNames);
    }

    [Fact]
    public void PreservesTransparentPixels()
    {
        // GIVEN transparent PNG pixels.
        using var input = CreateImage(MagickColors.Transparent, 8, 8);
        // WHEN processing the photograph.
        var result = processor.Process(input.ToByteArray(MagickFormat.Png));
        // THEN both outputs preserve alpha rather than replacing it with a background.
        foreach (var bytes in new[] { result.Detail, result.Thumbnail })
        {
            using var output = new MagickImage(bytes);
            Assert.True(output.HasAlpha);
            using var pixels = output.GetPixels();
            Assert.Equal(0, pixels.GetPixel(0, 0).ToColor()!.A);
        }
    }

    [Fact]
    public void ConvertsProfiledCmykAndRejectsUnprofiledCmyk()
    {
        // GIVEN a CMYK JPEG whose profile defines its colors.
        using var input = CreateImage(MagickColors.Red, 8, 8);
        input.TransformColorSpace(ColorProfiles.SRGB, ColorProfiles.USWebCoatedSWOP);
        // WHEN processing the profiled photograph.
        var result = processor.Process(input.ToByteArray(MagickFormat.Jpeg));
        // THEN output is sRGB and carries no profile or private metadata.
        using var output = new MagickImage(result.Detail);
        Assert.Equal(ColorSpace.sRGB, output.ColorSpace);
        Assert.Empty(output.ProfileNames);
        // AND the same CMYK pixels without a profile cannot silently change color.
        input.Strip();
        Assert.Equal(422, Assert.Throws<PhotoInputException>(() => processor.Process(input.ToByteArray(MagickFormat.Jpeg))).StatusCode);
    }

    [Theory]
    [InlineData("<svg xmlns='http://www.w3.org/2000/svg'/>")]
    [InlineData("%PDF-1.7")]
    [InlineData("https://example.com/picture.jpg")]
    [InlineData("GIF89a")]
    [InlineData("")]
    public void RejectsNonRasterSignatures(string text)
    {
        // GIVEN bytes for an unsupported format or external resource.
        var bytes = System.Text.Encoding.UTF8.GetBytes(text);
        // WHEN processing the alleged photograph.
        var exception = Assert.Throws<PhotoInputException>(() => processor.Process(bytes));
        // THEN it rejects the signature without invoking a delegate.
        Assert.Equal(415, exception.StatusCode);
    }

    [Fact]
    public void RejectsCorruptRasterAndOversizedUpload()
    {
        // GIVEN a truncated JPEG with a supported signature.
        var broken = new byte[] { 255, 216, 255, 0 };
        // WHEN processing the damaged image.
        var exception = Assert.Throws<PhotoInputException>(() => processor.Process(broken));
        // THEN malformed content differs from an oversized payload.
        Assert.Equal(422, exception.StatusCode);
        Assert.Equal(413, Assert.Throws<PhotoInputException>(() => processor.Process(new byte[PhotoProcessor.MaximumBytes + 1])).StatusCode);
    }

    [Fact]
    public void RejectsWebpAnimation()
    {
        // GIVEN an animated WebP containing two frames.
        using var frames = new MagickImageCollection();
        frames.Add(CreateImage(MagickColors.Red, 8, 8));
        frames.Add(CreateImage(MagickColors.Blue, 8, 8));
        foreach (var frame in frames) frame.AnimationDelay = 10;
        var bytes = frames.ToByteArray(MagickFormat.WebP);
        // WHEN the caller bypasses browser animation validation.
        var exception = Assert.Throws<PhotoInputException>(() => processor.Process(bytes));
        // THEN the server independently rejects animation.
        Assert.Equal(415, exception.StatusCode);
    }

    [Fact]
    public void RejectsJpegMultiPictureMetadataAfterFrameHeader()
    {
        // GIVEN a JPEG whose APP2 multi-picture marker occurs after its frame header.
        using var input = CreateImage(MagickColors.Red, 8, 8);
        var original = input.ToByteArray(MagickFormat.Jpeg);
        var scanOffset = -1;
        for (var index = 2; index + 1 < original.Length; index++)
        {
            if (original[index] == 255 && original[index + 1] == 0xda)
            {
                scanOffset = index;
                break;
            }
        }
        Assert.True(scanOffset > 0);
        using var multiplePictures = new MemoryStream();
        multiplePictures.Write(original.AsSpan(0, scanOffset));
        multiplePictures.Write(new byte[] { 255, 0xe2, 0, 6, (byte)'M', (byte)'P', (byte)'F', 0 });
        multiplePictures.Write(original.AsSpan(scanOffset));
        // WHEN a caller bypasses the browser single-frame check.
        var exception = Assert.Throws<PhotoInputException>(() => processor.Process(multiplePictures.ToArray()));
        // THEN the server rejects the MPF container instead of silently keeping its first picture.
        Assert.Equal(415, exception.StatusCode);
    }

    [Fact]
    public void RejectsApngAnimationControlChunk()
    {
        // GIVEN a PNG animation-control chunk before any frame data.
        var bytes = new byte[] { 137, 80, 78, 71, 13, 10, 26, 10, 0, 0, 0, 8, 97, 99, 84, 76, 0, 0, 0, 0, 0, 0, 0, 0 };
        // WHEN processing the animated upload.
        var exception = Assert.Throws<PhotoInputException>(() => processor.Process(bytes));
        // THEN animation is refused before raster decoding.
        Assert.Equal(415, exception.StatusCode);
    }

    [Fact]
    public void RejectsOversizedRasterDimensionsBeforeAllocatingPixels()
    {
        // GIVEN a PNG header claiming dimensions larger than the upload contract.
        using var input = CreateImage(MagickColors.Red, 8, 8);
        var bytes = input.ToByteArray(MagickFormat.Png);
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32BigEndian(bytes.AsSpan(16), 2049);
        uint crc = uint.MaxValue;
        foreach (var value in bytes.AsSpan(12, 17))
        {
            crc ^= value;
            for (var bit = 0; bit < 8; bit++) crc = (crc >> 1) ^ ((crc & 1) == 1 ? 0xedb88320u : 0);
        }
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32BigEndian(bytes.AsSpan(29), ~crc);
        // WHEN native decoding inspects its dimensions.
        var exception = Assert.Throws<PhotoInputException>(() => processor.Process(bytes));
        // THEN the resource bound rejects it before raster allocation.
        Assert.Equal(413, exception.StatusCode);
    }

    [Fact]
    public void NativePolicyDisablesUnneededCoders()
    {
        // GIVEN the processor has initialized its restrictive native policy.
        using var input = CreateImage(MagickColors.Red, 8, 8);
        // WHEN another call tries an unneeded GIF encoder.
        var exception = Assert.Throws<MagickPolicyErrorException>(() => input.ToByteArray(MagickFormat.Gif));
        // THEN the native policy itself blocks the coder, independently of signature checks.
        Assert.Contains("GIF", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void RejectsInvalidEmbeddedColorProfile()
    {
        // GIVEN a PNG with a syntactically valid iCCP chunk containing an invalid ICC profile.
        using var input = CreateImage(MagickColors.Red, 8, 8);
        var original = input.ToByteArray(MagickFormat.Png);
        using var compressed = new MemoryStream();
        compressed.Write(new byte[] { (byte)'x', 0, 0 });
        using (var encoder = new System.IO.Compression.ZLibStream(compressed, System.IO.Compression.CompressionLevel.Fastest, true))
            encoder.Write(new byte[132]);
        using var modified = new MemoryStream();
        modified.Write(original.AsSpan(0, 33));
        WriteChunk(modified, "iCCP", compressed.ToArray());
        modified.Write(original.AsSpan(33));
        // WHEN decoding cannot establish trustworthy color interpretation.
        var exception = Assert.Throws<PhotoInputException>(() => processor.Process(modified.ToArray()));
        // THEN the server rejects rather than silently dropping the invalid profile.
        Assert.Equal(422, exception.StatusCode);
    }

    private static MagickImage CreateImage(MagickColor color, uint width, uint height)
    {
        // Construct PNG bytes directly because the production policy also forbids the XC pseudo-coder.
        using var png = new MemoryStream();
        png.Write(new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 });
        var header = new byte[13];
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32BigEndian(header, width);
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32BigEndian(header.AsSpan(4), height);
        header[8] = 8;
        header[9] = 6;
        WriteChunk(png, "IHDR", header);
        using var compressed = new MemoryStream();
        using (var encoder = new System.IO.Compression.ZLibStream(compressed, System.IO.Compression.CompressionLevel.Fastest, true))
        {
            for (var row = 0; row < height; row++)
            {
                encoder.WriteByte(0);
                for (var column = 0; column < width; column++)
                    encoder.Write(new byte[] { color.R, color.G, color.B, color.A });
            }
        }
        WriteChunk(png, "IDAT", compressed.ToArray());
        WriteChunk(png, "IEND", []);
        return new MagickImage(png.ToArray());
    }

    private static void WriteChunk(Stream stream, string type, byte[] data)
    {
        var encoded = System.Text.Encoding.ASCII.GetBytes(type);
        Span<byte> integer = stackalloc byte[4];
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32BigEndian(integer, (uint)data.Length);
        stream.Write(integer);
        stream.Write(encoded);
        stream.Write(data);
        uint crc = uint.MaxValue;
        foreach (var value in encoded.Concat(data))
        {
            crc ^= value;
            for (var bit = 0; bit < 8; bit++) crc = (crc >> 1) ^ ((crc & 1) == 1 ? 0xedb88320u : 0);
        }
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32BigEndian(integer, ~crc);
        stream.Write(integer);
    }
}
