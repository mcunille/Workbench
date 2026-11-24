# Blob ingestion and validation

[Return to the main entry](blob-and-service-providers.md).

## Item photograph ingestion

The [collection guide](../collection.md) owns accepted source formats, browser preparation and
user-visible retry behavior. The following limits describe server and storage operations.

The API independently limits the prepared file to 4 MiB and 2,048 pixels per axis. Multipart
overhead is bounded to an additional 64 KiB, including requests without Content-Length. The
server reconstructs a WebP detail image and a thumbnail within 384 pixels; the combined encoded
output is bounded to 10 MiB. It removes EXIF/GPS, input ICC, XMP, comments, and embedded previews
after orientation/sRGB conversion. No original bytes or client filename are persisted.

Magick.NET Q8 is pinned in the release; bundled licenses ship in
`third-party-notices/Magick.NET.txt`. Only JPEG/PNG/WebP coders/modules are enabled, and delegates,
external paths, filters, and disk pixel-cache spill are disabled. One processor job per web
process uses at most two native threads. The 256 MiB native pixel-cache allocation budget is
not a hard process-RSS limit; codec allocations and the .NET process add memory. Processing has
cooperative ten-second progress cancellation and elapsed-time checks, while upload/storage uses
a two-minute cancellation deadline. Native code cannot be forcibly interrupted in-process.
These are bounded raster-reconstruction controls, not antivirus certification or process isolation.

Replacement first creates durable pending revisions and publishes immutable provider bytes. A
single SQL transaction then makes both images current and retires the previous pair. Removal
clears the item pointer and retires both attachments in the same transaction. The saved item is
never removed. Tenant-scoped request IDs, input digests, and item versions support exact retry;
competing commands return 409 instead of overwriting another session's result.

Unconfirmed outcomes return 503 and the client retains the exact operation for retry. The
server serializes identical retries across replicas and verifies staged/published bytes against
the regenerated output digest before finalization. A completely published concurrency loser is
retired under the normal grace period. Partial writes, a stale pending retry after a later item
change, or a different encoder output after a release change may require offline reconciliation.
Provider failures never authorize deletion of ambiguous bytes. Preserve pending revisions and
use the offline procedure below; do not mark them failed merely to unblock migration.

Logical replacement/removal hides earlier URLs immediately, while seven-day retention and holds
still apply. Include both retained variants in paired SQL/blob backups. Strict paired recovery remains
blocked if either required image is absent or has a mismatched digest. The separate
[SQL-authoritative recovery procedure](online-backup-recovery.md#manual-recovery) permits reviewed,
explicitly accepted missing-file dispositions while preserving SQL records. Do not advertise immediate physical
erasure or preservation of the original photo. Browser color conversion varies across devices;
these images support identification, not gemological color measurement.

## Acquisition document validation

Acquisition documents preserve the validated original bytes, including embedded image metadata.
Rasterizing paperwork would change the supplied evidence; download the validated original instead.
Attachment delivery avoids exposing inline PDF active content. Documents belong to the shared
acquisition rather than being duplicated per piece; correct content by adding a new file and
removing the mistaken one, keeping that correction explicit.
Each file is limited to 10 MiB; the multipart request allows another 64 KiB. Accepted raster formats
are single-frame JPEG, PNG, and WebP, with at most 40 million pixels and 12,000 pixels per axis.
The document validator shares the photograph native-processing semaphore. It temporarily raises
native dimension/profile limits only while holding that semaphore, then restores the photograph
limits of 2,048 pixels per axis and 4 MiB of profiles, including after validation failure. The same
256 MiB native cache budget, disabled disk/delegates/coders, and two-thread limit apply. Document
image decoding has cooperative two-minute progress cancellation. Images are not re-encoded or stripped.

PDF support is a deliberately restricted subset, implemented with pinned PdfPig 0.1.16 plus a bounded
original-syntax preflight. The packaged license and third-party terms ship in
`third-party-notices/PdfPig.txt`. PdfPig's strict parser silently normalizes duplicate dictionary
keys; the preflight therefore checks original indirect-object and cross-reference-trailer syntax,
including nested dictionaries and escaped names, before accepting the normalized object graph.
Every current cross-reference object and each page is inspected. Files must have 1–200 pages.

Supported PDF stream encodings are unfiltered data, Flate, ASCII85, and ASCIIHex. DCT/JPEG is accepted
only as the final encoding of a standard image object, with its JPEG bytes independently decoded by
the restricted image validator. Ordinary JPEG scan PDFs are covered by qualification tests. Compressed
object/cross-reference streams, inline images, stream prediction/decode parameters, other codecs
(including JBIG2, JPEG2000, CCITT, LZW, and RunLength), and unsupported object syntax are rejected.
Re-exporting to a PDF without those features or uploading a supported raster is required; acceptance
of every otherwise valid PDF is not claimed.

Encrypted files, action entry points (including otherwise benign actions), forms, JavaScript,
external file references, embedded files, 3D, PostScript, and rich media are rejected. No public blob
URL or inline PDF rendering is offered. These checks are format and active-content validation,
not antivirus certification.

PDF resource guards limit traversal to 50,000 current objects, 250,000 tokens, and depth 64. Flate
expansion is checked before the library decoder allocates output, with a 64 MiB per-stream limit;
ASCII85 input is bounded before its possible fourfold expansion. The aggregate decoded-stream budget
is 128 MiB. Predictor parameters are rejected before invoking library decoders because output-size
checks alone do not bound predictor row allocations or guarantee progress. One document-validation
job runs per process. Elapsed-time checks use a two-minute deadline; managed parser/native execution
is not hard-isolated or forcibly interrupted. Deployment process/container memory limits remain necessary.

Qualification fixtures cover valid raster/plain-PDF/JPEG-scan input, source-byte preservation,
encrypted PDFs with empty passwords, indirect/escaped action names, duplicate object/trailer keys,
unsupported codecs and xref streams, external/3D content, page/dimension limits, decompression and
predictor amplification, and native-limit restoration. A focused manual mutation removing the duplicate
key guard is detected by four tests; this is not an exhaustive mutation score or independent parser audit.
