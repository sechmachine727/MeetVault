---
name: progress-log
description: Record a durable per-contributor work log under docs/progress. Use when a work session in this repository changes code or docs, or produces findings the next agent needs.
---

# Progress log

Write down what you did, what you found, and what comes next, so the next agent starts from
evidence instead of rediscovering the session.

## When to write

Record entries for every work session in this repository, including sessions that only read,
search, or diagnose. A read-only session still produces findings worth keeping.

Update the file as you work. A session that dies mid-task must still leave a usable record.

## Where it goes

One folder per contributor, one file per day.

- Contributor folder: `docs/progress/progress_<git_username>/`, where `<git_username>` is the
  output of `git config user.name`, for example `progress_sechmachine`. Use the configured name
  exactly. Never substitute a GitHub handle, an email address, or a display name.
- Daily file: `PROGRESS_<YYYY-MM-DD>.md` inside that folder, using the local calendar date in
  ISO form, for example `PROGRESS_2026-10-07.md`. ISO dates sort chronologically as plain text.

Write into today's file. When the local date changes, create the next day's file and continue
there. One file covers exactly one contributor's one calendar day: never split a day across files
and never merge two days into one. Create the contributor folder the first time you write for
that contributor.

Progress files are committed. They are the repository's record of who did what and when.

## What to write

[TEMPLATE.md](TEMPLATE.md) defines the sections and the fields of a work-log entry. Copy it for a
new file and follow it.

Append new entries at the end and leave earlier ones intact. If an earlier entry turns out to be
wrong, add a new entry stating the correction. The record of what was believed, and when, is the
point of the file.

## Evidence

Every entry states how it was verified: the command and its result, a `file:line` reference, or an
explicit `not run: <reason>`. An entry that claims a result without naming its evidence is not
finished. Keep observed facts and inferences labelled as such.

## Prohibitions

- No secrets, API keys, tokens, connection strings, or personal data. Refer to them; do not paste
  them.
- No fabricated results. Never record a verification you did not run; write `not run:` and the
  reason.
- No pasted logs, stack traces, or command dumps. Quote the one line that carries the finding.
- No rewriting history. Correct an earlier entry by appending a correction, never by editing it
  away.

