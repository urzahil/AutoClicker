# Working on this repository

## Branch names

Name each branch after what it changes: a `feature/` or `fix/` prefix, then a short kebab-case description.

- `feature/` for new functionality, e.g. `feature/export-to-csv`
- `fix/` for bug fixes, e.g. `fix/crash-on-empty-input`

This applies even when a session was started on an auto-generated branch name: create a properly named branch and work there.

## Model assignment rules

- Architecture decisions and reviews: use Opus.
- Implementation tasks (new features, refactors): use Sonnet.
- Simple edits, formatting, renaming: use Haiku.
- Security-sensitive changes: always escalate to Opus for review.
