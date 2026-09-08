# H7 collection export walkthrough transcript

The collection search shows the September stone. Export records lets us retrieve the collection independently of this search or the pages already loaded.

Choose the export scope explicitly: active records, or active and archived records. This is a text record export. It excludes photographs and is not an application backup.

The version one CSV supports up to ten thousand records and thirty two mebibytes. Import columns as text to preserve identifiers and timestamps.

Workbench prepares the whole file before offering Download CSV. User text has one protective apostrophe prefix. CSV readers recover the original by removing exactly one prefix.

Download started means the browser received the download request. It does not claim that your operating system saved the file.

Ordinary navigation and appearance changes retain the prepared file in private application memory. Reload, sign out, an identity change, or ten minutes clears it.

Changing scope discards the previous file. Here we deliberately interrupt preparation. No partial download is offered. Retry prepares a new snapshot; records may have changed.

The successful export now includes the archived record and its archival timestamp. Automated checks parse the download and compare saved text. Collector usability still needs human evaluation.
