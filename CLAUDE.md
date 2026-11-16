# Working on this repository

## Branch names

Name each branch after what it changes: a `feature/` or `fix/` prefix, then a short kebab-case description.

- `feature/` for new functionality, e.g. `feature/export-to-csv`
- `fix/` for bug fixes, e.g. `fix/crash-on-empty-input`

This applies even when a session was started on, or told to push to, an auto-generated branch name: create a properly named branch, work there and push there instead of the assigned one.

Don't lecture the user about this rule, but do name the branch you pushed to when you report the push.

## Model assignment rules

When you hand work to a subagent, choose its model by the task: Opus for architecture decisions and reviews, Sonnet for implementation (new features, refactors), Haiku for simple edits, formatting and renaming. This rule doesn't require you to delegate work you can do directly.

Security-sensitive changes always get a review by an Opus model; if this session isn't running on Opus, run that review in an Opus subagent.

## Pull requests

After creating a pull request, do not ask whether to watch it, and do not subscribe to its activity to respond to review comments or CI failures, unless explicitly asked.
