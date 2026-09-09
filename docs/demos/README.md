# Walkthroughs and historical evidence

Current-source capture and rendering instructions are available for [archive recovery](h6/README.md),
[CSV export](h7/README.md), [collection packages](h8/README.md), and [acquisition](h9/README.md).
Generated recordings remain outside Git. Their transcripts and captions accompany those workflows;
recorded verification describes the dated implementation, not a fresh check of current source.

## Historical H1–H5 recordings

On 2026-09-08, a bounded local inventory found no MP4/WebM recordings in the main checkout's
`artifacts/`, local Codex worktrees, or September visualization directories. The inventory could not
read `60c3/Workbench/artifacts/self-host-live`; it does not establish that recordings are absent
elsewhere. H1 and H2 recordings remain in Git history, with matching [H1](h1/README.md) and [H2](h2/README.md)
companions and immutable media links. H3–H5 had no repository reproduction scripts or working media destinations, so their
orphan README, transcript, and caption files were retired together. No media was deleted.
The complete historical companions remain in Git at
[`9e618a1`](https://github.com/mcunille/Workbench/tree/9e618a1be40f47807c8164eab00d118305b9fdca/docs/demos).

The following observations were documented on 2026-09-07 and remain historical evidence:

- H1 (`159b71f`): the disposable-SQL scenario passed; representative creation, uncertain-save retry,
  and mobile frames were inspected, and the approximately 140-second recording and audio decoded.
- H2 (`ee34da4`): two browser tests and the recorded scenario passed. The 109-second recording decoded;
  desktop/light, 320px/dark, gallery, preview, retry, phone, and removal frames were inspected.
  Browser preparation resized a synthetic 4,096 × 3,072 PNG from 12,725,643 bytes to a
  2,048 × 1,536 WebP of 56,552 bytes. This does not predict typical camera-photo compression.
- H3 (`b7bf3ef`): the recorded SQL/storage scenario passed and its 69-second recording decoded;
  desktop/light, mobile/dark, search, retry recovery, and cleared-result frames were inspected.
- H4 (`e176204`): the approximately 76-second recording decoded, with desktop, mobile retry,
  conflict comparison, and reconciliation frames inspected. Four manual mutants were detected:
  SQL version predicate, client checked token, stale reconciliation initialization, and late cache
  invalidation. No automated mutation score was claimed.
- H5 (`9e618a1`): the approximately 96-second recording decoded; mobile uncertain outcome,
  desktop archived state, and photographed archived details at desktop/light and 320px/dark were
  inspected. Four manual mutants were detected: SQL version predicate, client expected token,
  collection invalidation, and uncertain-navigation protection. No automated mutation score was claimed.

These scenarios used synthetic data and disposable infrastructure, with authentication off camera.
Technical and visual checks do not establish collector usability or production acceptance. The
historical transcripts describe the capabilities of their original increment; current behavior is
specified in the [documentation index](../README.md).
