# The agent team

The humans decide; agents propose. The maintainer is the only one who merges to `master`.

## Roles

| Agent | Runs | Output |
|---|---|---|
| architect | on demand, weekly | ADRs, issues with acceptance criteria |
| dev | per issue, in parallel | one branch and PR per issue |
| reviewer | on open PRs | review comments only |
| qa | nightly | failing-test and coverage issues |
| security | weekly | `security` issues, dependency PRs |
| seo | weekly | site and README PRs |
| triage | daily | labels, stale-PR flags |
| dispatcher | daily | starts `dev` work on `ready` issues, max 5 open `agent` PRs |

Prompts live in `.claude/agents/`. Recurring runs are scheduled tasks (routines) that start a session with the role's prompt.

## Labels

`ready` (spec complete), `needs-spec`, `agent-working`, `agent` (PR made by an agent), `needs-human`, `bug`, `feature`, `docs`, `tests`, `security`. Defined in `.github/labels.yml` and synced by a workflow.

## Rules

- Agents never merge, approve or push to `master`. `CODEOWNERS` makes the maintainer the required reviewer.
- At most 5 open `agent` PRs, so the review queue stays small.
- Strongest model for architect and dev; cheaper models for the recurring routines.
- Branch protection on `master` (require PR, review, passing CI; restrict pushes) must be switched on by the maintainer in the repository settings; agents have no tool for it.
