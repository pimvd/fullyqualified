# Agent rules

Shared rules for all agents (Codex reads this file; Claude Code reads `CLAUDE.md`, which points here).

- Source of truth: `docs/PLAN.md` and the ADRs in `docs/adr`. Structural changes need an ADR approved by the architect.
- Everything is in English: code, docs, labels, commits.
- Stack: .NET 10, ASP.NET Core minimal APIs, EF Core + Npgsql (migrations from day one), Blazor, xUnit + Testcontainers.
- No hard-coded UI strings: use `IStringLocalizer` and resource files.
- Every table carries a `tenant_id`.
- Checks and cloud connectors are plugins behind one interface each.
- Monitoring and reporting only: never change anything in customer environments (until phase 4).
- Workflow: GitHub issues and pull requests only. Short-lived feature branches off `main`, squash merge. Nothing reaches `main` without review and a human merge. Agents never merge.
- Out of scope: issuing certificates, DMARC report processing, uptime monitoring, DNS provider APIs.
