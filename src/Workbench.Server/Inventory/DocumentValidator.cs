// Copyright (c) 2026 The White Stag Collection.

using System.Diagnostics;
using System.IO.Compression;
using ImageMagick;
using UglyToad.PdfPig;
using UglyToad.PdfPig.Core;
using UglyToad.PdfPig.Filters;
using UglyToad.PdfPig.Logging;
using UglyToad.PdfPig.Tokens;

namespace Workbench.Server.Inventory;

public sealed class DocumentValidator
{
    public const int MaximumBytes = 10 * 1024 * 1024;
    private static readonly SemaphoreSlim Capacity = new(1, 1);
    private static readonly HashSet<string> ActiveNames = new(StringComparer.Ordinal)
    {
        "JavaScript", "JS", "Launch", "URI", "GoToR", "GoToE", "SubmitForm", "ImportData",
        "EmbeddedFiles", "EmbeddedFile", "Filespec", "EF", "AF", "XFA", "RichMedia", "RichMediaContent",
        "Rendition", "Movie", "Sound", "OpenAction", "AA", "ObjStm", "PS", "Thread", "FS",
        "3D", "U3D", "PRC", "OnInstantiate", "3DA", "RichMediaExecute", "AcroForm", "FileAttachment", "Ref"
    };

    public ValidatedDocument Validate(byte[] content)
    {
        if (content.Length > MaximumBytes)
            throw new DocumentInputException(413, "The document exceeds 10 MiB.");
        if (!Capacity.Wait(0))
            throw new DocumentInputException(503, "Document validation is busy. Try again shortly.");
        try
        {
            var timer = Stopwatch.StartNew();
            if (content.AsSpan().StartsWith("%PDF-"u8))
            {
                ValidatePdf(content, timer);
                return new("application/pdf", "pdf");
            }
            return ValidateImage(content, timer);
        }
        finally
        {
            Capacity.Release();
        }
    }

    private static ValidatedDocument ValidateImage(byte[] content, Stopwatch timer)
    {
        PhotoProcessor.InitializeImagePolicy();
        if (!PhotoProcessor.Capacity.Wait(0))
            throw new DocumentInputException(503, "Image validation is busy. Try again shortly.");
        try
        {
            ResourceLimits.Width = 12000;
            ResourceLimits.Height = 12000;
            ResourceLimits.MaxProfileSize = MaximumBytes;
            var format = PhotoProcessor.DetectFormat(content);
            PhotoProcessor.CheckDimensions(content, format, 12000, 40_000_000);
            using var images = new MagickImageCollection();
            var warning = false;
            images.Warning += (_, _) => warning = true;
            images.Ping(content, new MagickReadSettings { Format = format });
            if (images.Count != 1)
                throw new DocumentInputException(415, "Use a single-frame document image.");
            if (images[0].Width > 12000 || images[0].Height > 12000 || (ulong)images[0].Width * images[0].Height > 40_000_000)
                throw new DocumentInputException(413, "Use an image with at most 40 million pixels and 12,000 pixels per edge.");
            using var image = new MagickImage();
            image.Warning += (_, _) => warning = true;
            // Native progress is cooperative; deployment memory limits remain a required boundary.
            image.Progress += (_, args) => args.Cancel = timer.Elapsed > TimeSpan.FromMinutes(2);
            image.Read(content, new MagickReadSettings { Format = format });
            if (image.Width > 12000 || image.Height > 12000 || (ulong)image.Width * image.Height > 40_000_000)
                throw new DocumentInputException(413, "Use an image with at most 40 million pixels and 12,000 pixels per edge.");
            if (warning || timer.Elapsed > TimeSpan.FromMinutes(2))
                throw new DocumentInputException(422, "The document image is damaged or could not be validated.");
            return format switch
            {
                MagickFormat.Jpeg => new("image/jpeg", "jpg"),
                MagickFormat.Png => new("image/png", "png"),
                _ => new("image/webp", "webp")
            };
        }
        catch (PhotoInputException exception)
        {
            throw new DocumentInputException(exception.StatusCode, exception.StatusCode == 413
                ? "Use an image with at most 40 million pixels and 12,000 pixels per edge."
                : "Use a valid single-frame JPEG, PNG, or WebP image, or a PDF document.");
        }
        catch (MagickResourceLimitErrorException)
        {
            throw new DocumentInputException(413, "The document exceeds image processing limits.");
        }
        catch (MagickException)
        {
            throw new DocumentInputException(422, "The document image is damaged or cannot be decoded.");
        }
        finally
        {
            ResourceLimits.Width = 2048;
            ResourceLimits.Height = 2048;
            ResourceLimits.MaxProfileSize = PhotoProcessor.MaximumBytes;
            PhotoProcessor.Capacity.Release();
        }
    }

    private static void ValidatePdf(byte[] content, Stopwatch timer)
    {
        try
        {
            var log = new ValidationLog();
            using var document = PdfDocument.Open(content, new ParsingOptions
            {
                UseLenientParsing = false,
                SkipMissingFonts = false,
                MaxStackDepth = 64,
                Logger = log,
                FilterProvider = new BoundedFilterProvider(timer)
            });
            if (document.IsEncrypted)
                throw InvalidPdf("Encrypted PDF documents are not supported.");
            if (document.NumberOfPages is < 1 or > 200)
                throw InvalidPdf("Use a PDF with between 1 and 200 pages.");
            if (document.Structure.CrossReferenceTable.ObjectOffsets.Count > 50_000)
                throw InvalidPdf("The PDF structure exceeds validation limits.");
            var syntax = new PdfSyntaxPreflight(content);
            foreach (var part in document.Structure.CrossReferenceTable.Parts)
            {
                if (part.Type != UglyToad.PdfPig.CrossReference.CrossReferenceType.Table)
                    throw InvalidPdf("Compressed PDF object and cross-reference streams are not supported.");
                syntax.Trailer(part.Offset);
            }
            foreach (var reference in document.Structure.CrossReferenceTable.ObjectOffsets.Keys)
            {
                var item = document.Structure.GetObject(reference);
                if (item.Position.Type != XrefEntryType.File)
                    throw InvalidPdf("Compressed PDF objects are not supported.");
                syntax.Object(item.Position.Value1, reference, item.Data is StreamToken);
            }
            var visited = new HashSet<IndirectReference>();
            var tokens = 0;
            void Inspect(IToken token, int depth)
            {
                if (++tokens > 250_000 || depth > 64 || timer.Elapsed > TimeSpan.FromMinutes(2))
                    throw InvalidPdf("The PDF structure exceeds validation limits.");
                switch (token)
                {
                    case IndirectReferenceToken reference:
                        if (visited.Add(reference.Data)) Inspect(document.Structure.GetObject(reference.Data).Data, depth + 1);
                        break;
                    case DictionaryToken dictionary:
                        foreach (var pair in dictionary.Data)
                        {
                            // Reject action entry points entirely, including unknown action subtypes.
                            // Passive destinations may remain, but document actions are not qualified.
                            if (pair.Key == "A" || ActiveNames.Contains(pair.Key))
                                throw InvalidPdf("Remove actions, active content, and embedded files from the PDF.");
                            Inspect(pair.Value, depth + 1);
                        }
                        break;
                    case ArrayToken array:
                        foreach (var value in array.Data) Inspect(value, depth + 1);
                        break;
                    case StreamToken stream:
                        if (stream.StreamDictionary.Data.ContainsKey("F"))
                            throw InvalidPdf("External PDF streams are not supported.");
                        RejectDecodeParameters(stream.StreamDictionary);
                        Inspect(stream.StreamDictionary, depth + 1);
                        var filters = document.Structure.FilterProvider.GetFilters(stream.StreamDictionary, document.Structure.TokenScanner);
                        var decoded = stream.Data;
                        for (var index = 0; index < filters.Count; index++)
                        {
                            if (filters[index] is DctDecodeFilter && index == filters.Count - 1
                                && stream.StreamDictionary.Data.TryGetValue("Subtype", out var subtype)
                                && subtype is NameToken { Data: "Image" })
                            {
                                if (!decoded.Span.StartsWith(new byte[] { 255, 216, 255 }))
                                    throw InvalidPdf("The PDF image is not a valid JPEG scan.");
                                _ = ValidateImage(decoded.ToArray(), timer);
                                break;
                            }
                            if (!filters[index].IsSupported) throw InvalidPdf("The PDF uses an unsupported stream encoding.");
                            decoded = filters[index].Decode(decoded, stream.StreamDictionary, document.Structure.FilterProvider, index);
                        }
                        break;
                    case NameToken name:
                        if (ActiveNames.Contains(name.Data)) throw InvalidPdf("Remove active content and embedded files from the PDF.");
                        break;
                    case StringToken or HexToken or NumericToken or BooleanToken or NullToken:
                        break;
                    default:
                        throw InvalidPdf("The PDF uses an unsupported object structure.");
                }
            }
            Inspect(document.Structure.Catalog.CatalogDictionary, 0);
            // Traverse every current xref object, including objects absent from the visible page graph.
            foreach (var reference in document.Structure.CrossReferenceTable.ObjectOffsets.Keys)
                if (visited.Add(reference)) Inspect(document.Structure.GetObject(reference).Data, 0);
            for (var page = 1; page <= document.NumberOfPages; page++)
            {
                if (document.GetPage(page).GetImages().Any(image => image.IsInlineImage))
                    throw InvalidPdf("Inline PDF images are not supported. Export images as standard PDF image objects.");
                if (timer.Elapsed > TimeSpan.FromMinutes(2)) throw InvalidPdf("PDF validation exceeded its time limit.");
            }
            if (log.HasIssue) throw InvalidPdf("The PDF is damaged or uses unsupported content.");
        }
        catch (DocumentInputException) { throw; }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            // Parser messages can contain supplied content and are deliberately not exposed.
            throw InvalidPdf("The PDF is damaged, encrypted, or uses unsupported content.");
        }
    }

    private static DocumentInputException InvalidPdf(string message) => new(422, message);

    private static void RejectDecodeParameters(DictionaryToken dictionary)
    {
        // PdfPig predictors may allocate from attacker-controlled row parameters before producing
        // output (and zero-width rows can fail to make progress). No predictor path is qualified.
        if (dictionary.Data.ContainsKey("DecodeParms") || dictionary.Data.ContainsKey("DP"))
            throw InvalidPdf("PDF stream prediction parameters are not supported. Re-export the document without prediction.");
    }

    private sealed class ValidationLog : ILog
    {
        public bool HasIssue { get; private set; }
        public void Debug(string message) { }
        public void Debug(string message, Exception exception) { HasIssue = true; }
        public void Warn(string message) { HasIssue = true; }
        public void Error(string message) { HasIssue = true; }
        public void Error(string message, Exception exception) { HasIssue = true; }
    }

    private sealed class BoundedFilterProvider : BaseFilterProvider
    {
        public BoundedFilterProvider(Stopwatch timer) : base(CreateFilters(timer)) { }

        private static Dictionary<string, IFilter> CreateFilters(Stopwatch timer)
        {
            var budget = new DecodingBudget();
            var flate = new BoundedFilter(new FlateFilter(), timer, budget, true);
            var ascii85 = new BoundedFilter(new Ascii85Filter(), timer, budget, false);
            var hex = new BoundedFilter(new AsciiHexDecodeFilter(), timer, budget, false);
            return new Dictionary<string, IFilter>
            {
                ["FlateDecode"] = flate,
                ["Fl"] = flate,
                ["ASCII85Decode"] = ascii85,
                ["A85"] = ascii85,
                ["ASCIIHexDecode"] = hex,
                ["AHx"] = hex,
                ["DCTDecode"] = new DctDecodeFilter(),
                ["DCT"] = new DctDecodeFilter()
            };
        }
    }

    private sealed class DecodingBudget
    {
        private long decodedBytes;
        public void Reserve(long count)
        {
            decodedBytes += count;
            if (decodedBytes > 128L * 1024 * 1024)
                throw InvalidPdf("The PDF stream data exceeds validation limits.");
        }
    }
    private sealed class BoundedFilter(IFilter inner, Stopwatch timer, DecodingBudget budget, bool flate) : IFilter
    {
        private const int MaximumDecodedBytes = 64 * 1024 * 1024;
        public bool IsSupported => true;
        public Memory<byte> Decode(Memory<byte> input, DictionaryToken dictionary, IFilterProvider provider, int index)
        {
            RejectDecodeParameters(dictionary);
            // ASCII85's 'z' abbreviation expands one byte to four; check before its allocator.
            if (inner is Ascii85Filter && input.Length > MaximumDecodedBytes / 4)
                throw InvalidPdf("The PDF stream exceeds pre-decode resource limits.");
            if (flate)
            {
                // Bound expansion before PdfPig's allocating decoder, including object/xref streams.
                using var source = new MemoryStream(input.ToArray(), false);
                using var decoder = new ZLibStream(source, CompressionMode.Decompress);
                Span<byte> buffer = stackalloc byte[8192];
                long total = 0;
                int read;
                while ((read = decoder.Read(buffer)) != 0)
                {
                    total += read;
                    if (total > MaximumDecodedBytes || timer.Elapsed > TimeSpan.FromMinutes(2))
                        throw InvalidPdf("The PDF stream exceeds validation limits.");
                }
            }
            var result = inner.Decode(input, dictionary, provider, index);
            budget.Reserve(result.Length);
            if (result.Length > MaximumDecodedBytes || timer.Elapsed > TimeSpan.FromMinutes(2))
                throw InvalidPdf("The PDF stream exceeds validation limits.");
            return result;
        }
    }
}

public sealed record ValidatedDocument(string MediaType, string Extension);

public sealed class DocumentInputException(int statusCode, string message) : Exception(message)
{
    public int StatusCode { get; } = statusCode;
}
