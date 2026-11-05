# Issue tracker: Local Markdown

Specs and tickets for this repository live as markdown files in `.scratch/`. Git ignores that folder, so nothing in it
is committed or published. Decisions that must last go to `docs/adr/` and to the phase's pull request description.

## Conventions

- One feature or phase per directory: `.scratch/<feature-slug>/`
- The spec is `.scratch/<feature-slug>/spec.md`
- Implementation tickets are one file per ticket at `.scratch/<feature-slug>/issues/<NN>-<slug>.md`, numbered from
  `01`, never a single combined tickets file
- Triage state is a `Status:` line near the top of each ticket file. Specs and tickets start as `ready-for-agent`. The
  other values are `needs-triage`, `needs-info`, `ready-for-human` and `wontfix`
- Comments and conversation history append to the bottom of the file under a `## Comments` heading

## When a skill says "publish to the issue tracker"

Create a new file under `.scratch/<feature-slug>/` (creating the directory if needed).

## When a skill says "fetch the relevant ticket"

Read the file at the referenced path. The user will normally pass the path or the issue number directly.
