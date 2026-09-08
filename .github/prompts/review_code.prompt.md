---
description: "Review uncommitted changes and report defects, worst first"
name: "review_code"
argument-hint: "Optional focus — a file, a module, or a risk area"
---

Review the changes currently in the working tree.

## What to look for

Defects, regressions, data-integrity problems, security risks, and coverage that is
missing where it matters. In that order of interest.

## How to report

- **Findings before summary.** If there is one real defect, it should be the first thing
  visible, not the conclusion of a description of the change.
- **Worst first.** Order by what would actually go wrong, not by file order.
- Each finding gets four things:
  1. What is wrong.
  2. What it costs at runtime or in maintenance — the failing case, concretely.
  3. The file and line.
  4. A fix worth applying, not a direction to think in.
- **Call out behavioural changes explicitly**, including ones that look like refactoring.
  A change that alters what the program does while claiming not to is the expensive kind.
- **Say where tests are missing**, but only where the untested path could plausibly break.
  A demand for coverage of everything is a demand that gets ignored.

## If there is nothing wrong

Say so plainly, then list what remains untested or uncertain. An empty review that says
"looks good" and an empty review that names three residual risks are not the same review.

## Focus

{{input}}
