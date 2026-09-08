---
name: spec-alignment
description: Check a request against the specs before implementing it, get consent before deviating, and move the spec in the same commit as the code.
---

# Spec alignment

## When this applies

Before implementing anything that can change behaviour, the data model, an interface, or a
workflow. In practice: new features, refactors that move a boundary, and any request that
sounds like it might already be described somewhere in [`docs/specs/`](../../../docs/specs/).

It does **not** apply to changes that cannot contradict a spec — a typo, a test that pins
existing behaviour, a comment.

## The workflow

### 1. Check before writing

Find the affected specs and architecture documents. Compare what is being asked against
what they require. State one of:

- **Aligned** — proceed.
- **Partially aligned** — some of it conflicts.
- **Not aligned** — the spec says otherwise.

### 2. If it conflicts, stop

Name the requirement, by spec and by line. Vagueness here defeats the point: "this might
deviate from the persistence spec" gives the user nothing to decide with.

Then offer both options — satisfy the spec as written, or change the spec deliberately —
and **wait**. Do not assume the spec is stale because it is inconvenient.

### 3. If a deviation is approved, ship the spec with the code

- Update the spec and move its version.
- Fix cross-references the change breaks.
- State in the change description what behaviour changed, which specs moved, and why it was
  approved.

## What this is guarding against

Not sloppiness — drift. Each individual departure from a spec looks reasonable in isolation,
and the accumulated result is a set of documents that describe a system nobody has.

Two failure modes worth naming:

- **Treating a placeholder as a requirement.** Several specs here are empty templates that
  say so. Nothing follows from them; do not derive an objection from a heading.
- **Updating the spec afterwards.** A spec that lands a commit later was wrong in between,
  and anyone reading the history sees code arriving without it.
