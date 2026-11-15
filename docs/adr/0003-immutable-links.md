# ADR-0003: immutable decisions and review instead of link rewriting

- Status: accepted
- Date: 2026-11-15
- Context: prototype v0.1

## Decision

An active source→master link is closed (`valid_to`) rather than updated in place. Every
link, unlink, rejection, creation and review decision is appended to `match_decisions`
with the ruleset version, actor, reason and JSON details. `match_evidence` stores the
scored explanation for each candidate.

A new observation cannot silently move a confirmed link. If a manual confirmation or
automated link would conflict with an existing active link, the operation fails with a
conflict and is surfaced for review.

Automatic linking is allowed only above clear score/margin thresholds and never across
squad markers or known sport/country conflicts. Ambiguous cases remain `pending`.

## Consequences

- The full history of why a source record belongs to a canonical team is queryable.
- Re-running the matcher after a ruleset change does not overwrite confirmed links.
- A human can close a wrong link and create a new one; both actions remain in the audit.
- The registry can accumulate superseded candidates and closed links; this is intentional
  evidence, not garbage.
