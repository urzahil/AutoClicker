# Working on this repository

## Branch names

Name each branch after what it changes: a `feature/` or `fix/` prefix, then a short kebab-case description.

- `feature/` for new functionality, e.g. `feature/export-to-csv`
- `fix/` for bug fixes, e.g. `fix/crash-on-empty-input`

This applies even when a session was started on an auto-generated branch name: create a properly named branch and work there.

Just follow this rule silently: do not remind the user about it or point out that the session started on an auto-generated branch name.

## Model assignment rules

- Architecture decisions and reviews: use Opus.
- Implementation tasks (new features, refactors): use Sonnet.
- Simple edits, formatting, renaming: use Haiku.
- Security-sensitive changes: always escalate to Opus for review.

## Pull requests

After creating a pull request, do not ask whether to watch it, and do not subscribe to its activity to respond to review comments or CI failures, unless explicitly asked.
