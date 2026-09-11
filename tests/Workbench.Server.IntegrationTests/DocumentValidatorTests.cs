// Copyright (c) 2026 The White Stag Collection.

using System.Text;
using Workbench.Server.Inventory;
using Workbench.Server.IntegrationTests.Infrastructure;
using Xunit;

namespace Workbench.Server.IntegrationTests;

[Collection(PhotoProcessorCollection.Name)]
public sealed class DocumentValidatorTests
{
    [Theory]
    [InlineData(ImageMagick.MagickFormat.Png, "image/png", "png")]
    [InlineData(ImageMagick.MagickFormat.Jpeg, "image/jpeg", "jpg")]
    [InlineData(ImageMagick.MagickFormat.WebP, "image/webp", "webp")]
    public void AcceptsOriginalSingleFrameImages(ImageMagick.MagickFormat format, string mediaType, string extension)
    {
        // GIVEN a supported image with embedded metadata.
        _ = new PhotoProcessor();
        using var image = PhotoProcessorTests.CreateImage(ImageMagick.MagickColors.Red, 8, 8);
        image.Comment = "original paperwork metadata";
        var bytes = image.ToByteArray(format);
        var original = bytes.ToArray();
        // WHEN validating without transforming the file.
        var result = new DocumentValidator().Validate(bytes);
        // THEN its format is identified and its bytes are preserved.
        Assert.Equal(new ValidatedDocument(mediaType, extension), result);
        Assert.Equal(original, bytes);
    }
    [Fact]
    public void AcceptsPlainPdfAndPreservesSource()
    {
        // GIVEN a structurally complete one-page PDF.
        var bytes = Pdf();
        var original = bytes.ToArray();
        // WHEN validating the supplied document.
        var result = new DocumentValidator().Validate(bytes);
        // THEN validated type is independent of filename and source bytes stay unchanged.
        Assert.Equal(new ValidatedDocument("application/pdf", "pdf"), result);
        Assert.Equal(original, bytes);
    }

    [Theory]
    [InlineData("/OpenAction 4 0 R", "<< /S /JavaScript /JS (alert\\(1\\)) >>")]
    [InlineData("/OpenAction 4 0 R", "<< /S /Launch /F (payload.exe) >>")]
    [InlineData("/OpenAction 4 0 R", "<< /S /URI /URI (https://example.com) >>")]
    [InlineData("/Names << /EmbeddedFiles 4 0 R >>", "<< /Names [(file) << /Type /Filespec /EF << /F 5 0 R >> >>] >>")]
    [InlineData("/Custom [<< /Nested 4 0 R >>]", "<< /S /Java#53cript /JS (alert\\(1\\)) >>")]
    public void RejectsIndirectActiveContent(string catalog, string fourthObject)
    {
        // GIVEN an action or embedded file reached through indirect or nested objects.
        var bytes = Pdf(catalog, fourthObject);
        // WHEN parsing the entire document graph.
        var error = Assert.Throws<DocumentInputException>(() => new DocumentValidator().Validate(bytes));
        // THEN it rejects private-file active content.
        Assert.Equal(422, error.StatusCode);
    }

    [Fact]
    public void RejectsMalformedAndOversizedDocuments()
    {
        // GIVEN a PDF signature without a valid structure and an excessive upload.
        var validator = new DocumentValidator();
        // WHEN validating them.
        var malformed = Assert.Throws<DocumentInputException>(() => validator.Validate("%PDF-1.7\n"u8.ToArray()));
        var oversized = Assert.Throws<DocumentInputException>(() => validator.Validate(new byte[DocumentValidator.MaximumBytes + 1]));
        // THEN format and upload limits are independently enforced.
        Assert.Equal(422, malformed.StatusCode);
        Assert.Equal(413, oversized.StatusCode);
    }

    [Fact]
    public void RejectsEncryptedPdfEvenWithEmptyUserPassword()
    {
        // GIVEN valid standard RC4 PDF encryption allowing opening with an empty password.
        var padding = Convert.FromHexString("28BF4E5E4E758A4164004E56FFFA01082E2E00B6D0683E802F0CA9FE6453697A");
        var ownerPassword = Encoding.ASCII.GetBytes("owner").Concat(padding).Take(32).ToArray();
        var owner = Rc4(System.Security.Cryptography.MD5.HashData(ownerPassword)[..5], padding);
        var identifier = new byte[16];
        var material = padding.Concat(owner).Concat(BitConverter.GetBytes(-4)).Concat(identifier).ToArray();
        var user = Rc4(System.Security.Cryptography.MD5.HashData(material)[..5], padding);
        var bytes = Pdf("", $"<< /Filter /Standard /V 1 /R 2 /O <{Convert.ToHexString(owner)}> /U <{Convert.ToHexString(user)}> /P -4 >>",
            "/Encrypt 4 0 R /ID [<00000000000000000000000000000000> <00000000000000000000000000000000>]");
        using var parsed = UglyToad.PdfPig.PdfDocument.Open(bytes);
        Assert.True(parsed.IsEncrypted);
        // WHEN the validator sees a readable but encrypted file.
        var error = Assert.Throws<DocumentInputException>(() => new DocumentValidator().Validate(bytes));
        // THEN encryption is rejected regardless of password requirements.
        Assert.Equal(422, error.StatusCode);
    }

    private static byte[] Rc4(byte[] key, byte[] input)
    {
        var state = Enumerable.Range(0, 256).ToArray();
        var j = 0;
        for (var i = 0; i < 256; i++)
        {
            j = (j + state[i] + key[i % key.Length]) % 256;
            (state[i], state[j]) = (state[j], state[i]);
        }
        var result = new byte[input.Length];
        var x = 0;
        j = 0;
        for (var index = 0; index < input.Length; index++)
        {
            x = (x + 1) % 256;
            j = (j + state[x]) % 256;
            (state[x], state[j]) = (state[j], state[x]);
            result[index] = (byte)(input[index] ^ state[(state[x] + state[j]) % 256]);
        }
        return result;
    }
    [Fact]
    public void AllowsPaperworkBeyondPhotographAxisLimit()
    {
        // GIVEN a single-row scan wider than the photograph policy but within paperwork limits.
        _ = new PhotoProcessor();
        var bytes = PhotoProcessorTests.CreatePng(ImageMagick.MagickColors.Red, 3000, 1);
        // WHEN validating the document and attempting to use it as a prepared photograph.
        var result = new DocumentValidator().Validate(bytes);
        var photoError = Assert.Throws<PhotoInputException>(() => new PhotoProcessor().Process(bytes));
        // THEN document support does not relax the photograph contract.
        Assert.Equal("image/png", result.MediaType);
        Assert.Equal(413, photoError.StatusCode);
    }

    [Theory]
    [InlineData(12001u, 1u)]
    [InlineData(10000u, 5000u)]
    public void RejectsExcessiveDimensionsBeforeDecode(uint width, uint height)
    {
        // GIVEN a header claiming an excessive axis or pixel count.
        _ = new PhotoProcessor();
        using var image = PhotoProcessorTests.CreateImage(ImageMagick.MagickColors.Red, 8, 8);
        var bytes = image.ToByteArray(ImageMagick.MagickFormat.Png);
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32BigEndian(bytes.AsSpan(16), width);
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32BigEndian(bytes.AsSpan(20), height);
        // WHEN inspecting the source before native pixel allocation.
        var error = Assert.Throws<DocumentInputException>(() => new DocumentValidator().Validate(bytes));
        // THEN resource admission rejects it independently of its damaged checksum.
        Assert.Equal(413, error.StatusCode);
    }

    [Fact]
    public void RejectsAnimatedWebp()
    {
        // GIVEN two image frames in a supported container.
        _ = new PhotoProcessor();
        using var frames = new ImageMagick.MagickImageCollection();
        frames.Add(PhotoProcessorTests.CreateImage(ImageMagick.MagickColors.Red, 8, 8));
        frames.Add(PhotoProcessorTests.CreateImage(ImageMagick.MagickColors.Blue, 8, 8));
        var bytes = frames.ToByteArray(ImageMagick.MagickFormat.WebP);
        // WHEN validating paperwork.
        var error = Assert.Throws<DocumentInputException>(() => new DocumentValidator().Validate(bytes));
        // THEN multiple frames are rejected.
        Assert.Equal(415, error.StatusCode);
    }

    [Fact]
    public void RejectsUnresolvedReferencesAndExcessiveNesting()
    {
        // GIVEN a dangling object and a deeply nested array in otherwise complete PDFs.
        var invalidReference = Pdf("/Custom 99 0 R");
        var nested = Pdf("/Custom " + new string('[', 80) + "0" + new string(']', 80));
        // WHEN traversing structure, not just extracting visible text.
        var first = Assert.Throws<DocumentInputException>(() => new DocumentValidator().Validate(invalidReference));
        var second = Assert.Throws<DocumentInputException>(() => new DocumentValidator().Validate(nested));
        // THEN both unsupported structures fail closed.
        Assert.Equal(422, first.StatusCode);
        Assert.Equal(422, second.StatusCode);
    }
    [Fact]
    public void AcceptsJpegScanInsidePdf()
    {
        // GIVEN a PDF containing a common JPEG receipt scan.
        _ = new PhotoProcessor();
        using var image = PhotoProcessorTests.CreateImage(ImageMagick.MagickColors.Red, 32, 32);
        var builder = new UglyToad.PdfPig.Writer.PdfDocumentBuilder();
        var page = builder.AddPage(UglyToad.PdfPig.Content.PageSize.A4);
        page.AddJpeg(image.ToByteArray(ImageMagick.MagickFormat.Jpeg), new UglyToad.PdfPig.Core.PdfRectangle(0, 0, 100, 100));
        var bytes = builder.Build();
        // WHEN validating the scan and all PDF objects.
        var result = new DocumentValidator().Validate(bytes);
        // THEN this supported document remains downloadable in its original format.
        Assert.Equal("application/pdf", result.MediaType);
    }
    [Theory]
    [InlineData(200, true)]
    [InlineData(201, false)]
    public void EnforcesPdfPageLimit(int count, bool accepted)
    {
        // GIVEN a complete PDF at or beyond the page-count boundary.
        var builder = new UglyToad.PdfPig.Writer.PdfDocumentBuilder();
        for (var page = 0; page < count; page++) builder.AddPage(UglyToad.PdfPig.Content.PageSize.A4);
        var bytes = builder.Build();
        // WHEN validating the entire PDF.
        if (accepted)
        {
            // THEN the exact limit is usable.
            Assert.Equal("application/pdf", new DocumentValidator().Validate(bytes).MediaType);
        }
        else
        {
            // THEN additional pages are rejected.
            Assert.Equal(422, Assert.Throws<DocumentInputException>(() => new DocumentValidator().Validate(bytes)).StatusCode);
        }
    }

    [Fact]
    public void RejectsCompressedExpansionBeforeUnboundedAllocation()
    {
        // GIVEN a small compressed stream expanding beyond the per-stream memory budget.
        using var output = new MemoryStream();
        using (var encoder = new System.IO.Compression.ZLibStream(output, System.IO.Compression.CompressionLevel.SmallestSize, true))
        {
            var block = new byte[8192];
            for (var index = 0; index < 65 * 128; index++) encoder.Write(block);
        }
        var data = output.ToArray();
        var bytes = Pdf("", $"<< /Length {data.Length} /Filter /FlateDecode >>\nstream\n{Encoding.Latin1.GetString(data)}\nendstream");
        // WHEN validating every xref object, including non-page streams.
        var error = Assert.Throws<DocumentInputException>(() => new DocumentValidator().Validate(bytes));
        // THEN expansion is stopped by the bounded preflight.
        Assert.Equal(422, error.StatusCode);
        Assert.Contains("limit", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void RejectsUnsupportedStreamEncoding()
    {
        // GIVEN an unqualified PDF stream codec.
        var bytes = Pdf("", "<< /Length 1 /Filter /JBIG2Decode >>\nstream\nx\nendstream");
        // WHEN validating all streams.
        var error = Assert.Throws<DocumentInputException>(() => new DocumentValidator().Validate(bytes));
        // THEN unsupported parsing is never treated as success.
        Assert.Equal(422, error.StatusCode);
    }
    [Fact]
    public void RejectsDuplicateDictionaryKeys()
    {
        // GIVEN ambiguous duplicate action entries that different readers could resolve differently.
        var bytes = Pdf("/Custom 4 0 R", "<< /Type /Action /S /JavaScript /S /GoTo /D [3 0 R /Fit] >>");
        // WHEN strict parsing resolves the dictionary.
        var error = Assert.Throws<DocumentInputException>(() => new DocumentValidator().Validate(bytes));
        // THEN an ambiguous active-content interpretation is not accepted.
        Assert.Equal(422, error.StatusCode);
    }
    [Fact]
    public void RejectsUnqualifiedInlineImages()
    {
        // GIVEN image data inside page content rather than a separate image object.
        const string content = "BI /W 1 /H 1 /CS /RGB /BPC 8 ID abc EI";
        var bytes = Pdf("", $"<< /Length {content.Length} >>\nstream\n{content}\nendstream", pageExtra: "/Contents 4 0 R");
        // WHEN inspecting the parsed page as well as xref objects.
        var error = Assert.Throws<DocumentInputException>(() => new DocumentValidator().Validate(bytes));
        // THEN an unqualified inline image path fails closed.
        Assert.Equal(422, error.StatusCode);
    }
    [Theory]
    [InlineData("/Custom [<< /N 1 /#4E 2 >>]")]
    [InlineData("/Custom << /S /GoTo /#53 /GoTo >>")]
    public void RejectsNestedAndEscapedDuplicateKeys(string catalog)
    {
        // GIVEN duplicate names hidden in nested values or encoded names.
        var bytes = Pdf(catalog);
        // WHEN checking original object syntax before trusting normalized tokens.
        var error = Assert.Throws<DocumentInputException>(() => new DocumentValidator().Validate(bytes));
        // THEN equivalent spellings cannot conceal duplicate fields.
        Assert.Equal(422, error.StatusCode);
    }

    [Fact]
    public void RejectsDuplicateTrailerKeys()
    {
        // GIVEN duplicate root pointers in a parseable trailer.
        var bytes = Pdf(trailer: "/Root 1 0 R");
        // WHEN checking all cross-reference trailers.
        var error = Assert.Throws<DocumentInputException>(() => new DocumentValidator().Validate(bytes));
        // THEN trailer ambiguity is rejected as well as object ambiguity.
        Assert.Equal(422, error.StatusCode);
    }

    [Fact]
    public void AcceptsNestedPassiveValuesWithoutInterpretingStringsAsSyntax()
    {
        // GIVEN ordinary literal/hex strings, escaped names, arrays, comments, and references.
        var bytes = Pdf("/Custom [<< /#4E [(escaped \\(parenthesis\\) and << /S /JS >>) <4142> true null .5 -1 3 0 R] >> %comment\n ]");
        // WHEN checking PDF structure and original dictionary syntax.
        var result = new DocumentValidator().Validate(bytes);
        // THEN text contents are never mistaken for active dictionaries or duplicate names.
        Assert.Equal("application/pdf", result.MediaType);
    }

    [Fact]
    public void RejectsCompressedCrossReferenceStreamsExplicitly()
    {
        // GIVEN a valid modern cross-reference stream, outside the qualified syntax preflight.
        var bytes = CrossReferenceStreamPdf();
        using var parsed = UglyToad.PdfPig.PdfDocument.Open(bytes);
        Assert.Equal(1, parsed.NumberOfPages);
        // WHEN validating its original structure.
        var error = Assert.Throws<DocumentInputException>(() => new DocumentValidator().Validate(bytes));
        // THEN unsupported parsing is explicit rather than silently skipping duplicate checks.
        Assert.Equal(422, error.StatusCode);
        Assert.Contains("cross-reference", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    private static byte[] CrossReferenceStreamPdf()
    {
        using var output = new MemoryStream();
        void Write(string value) => output.Write(Encoding.ASCII.GetBytes(value));
        Write("%PDF-1.7\n");
        var objects = new[]
        {
            "<< /Type /Catalog /Pages 2 0 R >>",
            "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] /Resources << >> >>"
        };
        var offsets = new List<long>();
        for (var index = 0; index < objects.Length; index++)
        {
            offsets.Add(output.Position);
            Write($"{index + 1} 0 obj\n{objects[index]}\nendobj\n");
        }
        offsets.Add(output.Position);
        var entries = new byte[35];
        entries[5] = 255;
        entries[6] = 255;
        for (var index = 0; index < offsets.Count; index++)
        {
            entries[(index + 1) * 7] = 1;
            System.Buffers.Binary.BinaryPrimitives.WriteUInt32BigEndian(entries.AsSpan((index + 1) * 7 + 1), (uint)offsets[index]);
        }
        Write("4 0 obj\n<< /Type /XRef /Size 5 /W [1 4 2] /Root 1 0 R /Length 35 >>\nstream\n");
        output.Write(entries);
        Write($"\nendstream\nendobj\nstartxref\n{offsets[^1]}\n%%EOF\n");
        return output.ToArray();
    }
    [Theory]
    [InlineData("<< /S /Thread /F (external.pdf) /D 0 >>")]
    [InlineData("<< /Subtype /FileAttachment /FS (external.pdf) >>")]
    [InlineData("<< /Ref << /F (external.pdf) /Page 0 >> >>")]
    [InlineData("<< /AcroForm << >> >>")]
    [InlineData("<< /Type /3D /Subtype /U3D /OnInstantiate 3 0 R >>")]
    [InlineData("<< /A << /S /UnknownFutureAction /F (external.pdf) >> >>")]
    public void RejectsOtherExecutableAndExternalContent(string payload)
    {
        // GIVEN external article actions, 3D scripts, or unqualified action types.
        var bytes = Pdf("/Custom 4 0 R", payload);
        // WHEN inspecting actions beyond JavaScript's common entry point.
        var error = Assert.Throws<DocumentInputException>(() => new DocumentValidator().Validate(bytes));
        // THEN unsupported executable content cannot pass as an inert dictionary.
        Assert.Equal(422, error.StatusCode);
    }
    [Theory]
    [InlineData(1000000)]
    [InlineData(0)]
    public void RejectsUnqualifiedPredictorBeforeLibraryAllocation(int columns)
    {
        // GIVEN a small, safe reproduction of predictor-driven row allocation amplification.
        using var output = new MemoryStream();
        using (var encoder = new System.IO.Compression.ZLibStream(output, System.IO.Compression.CompressionLevel.Fastest, true))
            encoder.WriteByte(0);
        var data = output.ToArray();
        var bytes = Pdf("", $"<< /Length {data.Length} /Filter /FlateDecode /DecodeParms << /Predictor 2 /Colors 1 /BitsPerComponent 8 /Columns {columns} >> >>\nstream\n{Encoding.Latin1.GetString(data)}\nendstream");
        // WHEN decoding a stream whose expansion size alone cannot bound predictor allocation.
        var error = Assert.Throws<DocumentInputException>(() => new DocumentValidator().Validate(bytes));
        // THEN unsupported prediction parameters are rejected before invoking the library decoder.
        Assert.Equal(422, error.StatusCode);
    }
    [Fact]
    public void RestoresNativePhotographLimitsAfterDocumentFailure()
    {
        // GIVEN the shared native decoder policy and a malformed document image.
        _ = new PhotoProcessor();
        var validator = new DocumentValidator();
        // WHEN document validation exits through its error path.
        Assert.Throws<DocumentInputException>(() => validator.Validate(new byte[] { 255, 216, 255, 0 }));
        // THEN later photograph decoding retains its original native allocation boundary.
        Assert.Equal(2048UL, ImageMagick.ResourceLimits.Width);
        Assert.Equal(2048UL, ImageMagick.ResourceLimits.Height);
        Assert.Equal((ulong)PhotoProcessor.MaximumBytes, ImageMagick.ResourceLimits.MaxProfileSize);
    }
    [Fact]
    public void RejectsChainedAscii85ExpansionBeforeDecoderAllocation()
    {
        // GIVEN a tiny compressed representation of many four-byte ASCII85 zero abbreviations.
        using var output = new MemoryStream();
        using (var encoder = new System.IO.Compression.ZLibStream(output, System.IO.Compression.CompressionLevel.Fastest, true))
        {
            var block = Enumerable.Repeat((byte)'z', 8192).ToArray();
            for (var index = 0; index < 17 * 128; index++) encoder.Write(block);
        }
        var data = output.ToArray();
        var bytes = Pdf("", $"<< /Length {data.Length} /Filter [/FlateDecode /ASCII85Decode] >>\nstream\n{Encoding.Latin1.GetString(data)}\nendstream");
        // WHEN a chained codec would amplify the already decoded buffer beyond its budget.
        var error = Assert.Throws<DocumentInputException>(() => new DocumentValidator().Validate(bytes));
        // THEN the wrapper refuses it before the second decoder allocates its output.
        Assert.Equal(422, error.StatusCode);
        Assert.Contains("pre-decode", error.Message, StringComparison.Ordinal);
    }
    internal static byte[] Pdf(string catalog = "", string? fourthObject = null, string trailer = "", string pageExtra = "")
    {
        var objects = new List<string>
        {
            $"<< /Type /Catalog /Pages 2 0 R {catalog} >>",
            "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
            $"<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] /Resources << >> {pageExtra} >>"
        };
        if (fourthObject is not null) objects.Add(fourthObject);
        var output = new StringBuilder("%PDF-1.7\n");
        var offsets = new List<int>();
        for (var index = 0; index < objects.Count; index++)
        {
            offsets.Add(Encoding.ASCII.GetByteCount(output.ToString()));
            output.Append($"{index + 1} 0 obj\n{objects[index]}\nendobj\n");
        }
        var xref = Encoding.ASCII.GetByteCount(output.ToString());
        output.Append($"xref\n0 {objects.Count + 1}\n0000000000 65535 f \n");
        foreach (var offset in offsets) output.Append($"{offset:0000000000} 00000 n \n");
        output.Append($"trailer\n<< /Size {objects.Count + 1} /Root 1 0 R {trailer} >>\nstartxref\n{xref}\n%%EOF\n");
        return Encoding.Latin1.GetBytes(output.ToString());
    }
}
