# 2. Report libraries and job queue

Status: proposed

- **Excel:** ClosedXML (MIT).
- **PDF:** PDFsharp/MigraDoc 6.2.x (MIT), with DejaVu Sans embedded because Linux containers ship no fonts. Stay on 6.2.x: from 6.5 the package is AGPL-3.0 with a license check, and QuestPDF was dropped because its Community license caps revenue.
- **Jobs:** a `jobs` table in Postgres claimed with `FOR UPDATE SKIP LOCKED` by BackgroundService workers. A unique index on (asset, check type) keeps at most one queued job per check. Failed checks are retried after 15 minutes, healthy ones are rescanned every `Scanning:IntervalHours` (default 6).
- **Summaries:** results store a resource key plus arguments, not display text, so the UI stays localizable (nl, en).
