# H2 narrated walkthrough

[Transcript](transcript.md) · [Captions](captions.srt)

Historical recording from 2026-09-07 at revision `ee34da4898b1bf5ac7b61783ed596f9773e49265`.
[View or download the original recording](https://github.com/mcunille/Workbench/blob/ee34da4898b1bf5ac7b61783ed596f9773e49265/docs/demos/h2/h2-walkthrough.mp4).
The transcript and captions match this retained media. Capabilities and verification below describe
that historical increment; consult the [documentation index](../../README.md) for current behavior.
The recording is no longer tracked at the branch tip and has no current-source reproduction script.

The recording exercises the real photo workflow against a disposable SQL Server database and private local blob storage. Authentication happens off camera. The gemstone illustrations are synthetic demonstration fixtures, not photographs of real possessions. Narration uses Microsoft Zira, a synthetic voice.

The walkthrough shows local image preparation and preview, explicit upload, persistence after reload and in another authenticated session, Grid/List display, a replacement whose response is deliberately lost after the real commit, safe retry, mobile appearance, and confirmed removal.

## Verification

The recorded scenario passed against the real application with disposable SQL and private blob storage. The two H2 browser tests passed. Desktop/light, 320px/dark, gallery, preview, retry, phone, and removal frames were inspected. The 1 minute 49 second MP4 was fully decoded. Browser preparation resized the synthetic 4,096 x 3,072 PNG from 12,725,643 bytes to a 2,048 x 1,536 WebP of 56,552 bytes; this is not a claim about typical camera photographs.
