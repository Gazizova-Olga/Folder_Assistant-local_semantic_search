# AGENTS

Instructions for agents and contributors working in this repository.

## The spec-alignment gate

**Specifications are the source of truth. Code that contradicts one is blocked until either
the request changes or the spec does.**

This applies before implementing anything that changes behaviour, the data model, an
interface, or a workflow.

### 1. Check alignment first

- Find the affected specs in [`docs/specs/`](docs/specs/) and the architecture documents in
  [`docs/diagrams/`](docs/diagrams/).
- Compare what is being asked against what they require.
- State a verdict: **Aligned**, **Partially aligned**, or **Not aligned**.

### 2. If it is not aligned, stop and ask

Name the requirement that conflicts — not "this may deviate", but which spec and which
line. Then present the choice explicitly:

- **Stay aligned** — an alternative that satisfies the spec as written.
- **Update the spec** — change the requirement, deliberately.

**Wait for approval.** Do not implement spec-violating behaviour on the assumption that the
spec is out of date. If approval is withheld, propose the aligned alternative.

### 3. If it is approved, update the spec in the same commit

- Revise the spec, and move its version.
- Fix any cross-references the change invalidates.
- Say in the change description what behaviour changed, which specs moved, and why it was
  approved.

A spec updated in a later commit is a spec that was wrong in between, and the history will
show the code arriving without it.

## Why this exists

A specification that disagrees with the code is worse than no specification, because it is
believed. The gate is not process for its own sake — it is what keeps the documents worth
reading.

The corollary matters as much: **a placeholder spec is not a requirement.** Several specs in
this repository say "not yet written" at the top. Those are admissions, not descriptions,
and nothing should be inferred from them.
