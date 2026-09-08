# H3 narrated walkthrough

[Transcript](transcript.md) · [Captions](captions.srt)

The recorded video is an external artifact, not a reproducible repository build.

The recording exercises the real search API and UI against disposable SQL Server and private
local blob storage. Authentication happens off camera. The records and gemstone illustration are
synthetic demonstration fixtures; narration uses Microsoft Zira, a synthetic voice.

It shows case-insensitive notes search, Grid/List presentation with a thumbnail, mobile details
and return-state restoration, appearance switching, an injected search failure and explicit retry,
and clearing a no-match search without modifying records. Automated browser regressions separately
exercise later-page results and position restoration at 320px and 1280px.

## Verification

The recorded scenario passed against disposable SQL and storage. The 69-second MP4 was fully decoded. Desktop/light, mobile/dark, search, retry recovery, and cleared-result frames were visually inspected.
