# Diagram adjustments

Every change to a document in `docs/diagrams/` is recorded here, dated, with its reasoning.

**The rule: a diff shows what changed; only these documents say why.** A diagram that has drifted
from the system is worse than no diagram, because it is believed — and the drift is never visible
in the diagram itself, only in the gap between it and the code. Recording the reasoning next to the
diagrams, rather than in a commit message someone would have to go looking for, is what makes that
gap checkable later.

They live beside the diagrams they describe, not under `docs/`, for the same reason: a reader who
opens `docs/diagrams/` should find the record without knowing it exists.

## Scope

- **Diagrams are recorded here.** They carry no version header of their own, so without these
  documents a changed diagram has no history but its diff.
- **Specs are not.** Each spec carries its own version and last-updated line in its Document
  Control header, and is revised in place. A second record kept elsewhere would give it two
  histories that can disagree — which is the failure these documents exist to prevent, reintroduced
  one level up.

## Archived copies

Where a diagram is rewritten substantially rather than amended, the pre-edit copy is archived
verbatim under `docs/archive/<date>/`, so the version a given adjustments document argues against
can still be read. The archive is a snapshot, never a second live copy: nothing links to it as
current, and it is not updated again.
