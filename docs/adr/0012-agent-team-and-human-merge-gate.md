# 12. Agent team with a human merge gate

Status: proposed. Builds on the workflow rule in AGENTS.md.

- **Decision.** Work is done by a team of role-based agents (`docs/team.md`, prompts in `.claude/agents/`) coordinated only through GitHub issues, labels and pull requests. The maintainer is the single merge authority for `master`.
- **Why.** Throughput comes from parallel agents; safety comes from one human gate. Labels make the queue legible and let routines act without a shared state store.
- **Enforcement.** `CODEOWNERS` requires the maintainer's review. Branch protection (PR required, review required, CI green, no direct pushes) is a repository setting the maintainer enables; until then the rule is convention plus the agent prompts, which forbid merging and approving.
- **Limits.** At most 5 open `agent` PRs; the dispatcher stops at the cap. The reviewer only comments.
- **Routines.** triage and dispatcher daily, qa nightly, security and seo weekly, as scheduled tasks. Each starts a fresh session and must be idempotent (check for existing issues and PRs first).
- **Not included:** auto-merge, agent approvals, agents changing repository settings, a custom orchestrator.
