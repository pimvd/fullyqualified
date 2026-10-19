# 2. Report libraries and job queue

Status: proposed

- **Excel:** ClosedXML (MIT).
- **PDF:** QuestPDF under its Community license. It is free below the revenue threshold; revisit before the hosted launch generates revenue above it.
- **Jobs:** a `jobs` table in Postgres claimed with `FOR UPDATE SKIP LOCKED` by BackgroundService workers. A unique index on (asset, check type) keeps at most one queued job per check. Failed checks are retried after 15 minutes, healthy ones are rescanned every `Scanning:IntervalHours` (default 6).
- **Summaries:** results store a resource key plus arguments, not display text, so the UI stays localizable (nl, en).
