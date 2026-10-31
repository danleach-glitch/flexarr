# Readarr-family fixtures

- **Recorded from Bookshelf** (`ghcr.io/pennydreadful/bookshelf:hardcover`, 0.4.21.182) on 2026-10-31: `webhook-test.json`,
  `webhook-health.json`, `status-bookshelf.json`, `rootfolder.json`, `qualityprofile.json`, `queue-empty.json`. Bookshelf calls
  itself `Readarr` in `appName`; `packageAuthor` (`pennydreadful`) tells it apart.
- **Written from Readarr's source** (`src/NzbDrone.Core/Notifications/Webhook/*`, which Bookshelf shares): every other
  `webhook-*.json`. Bookshelf's metadata search failed that day, so it couldn't add an author to send them for real.
- `webhook-download-chaptarr.json`: Chaptarr's provider-prefixed ids (`hc:`) and an ebook file beside the audiobook.
