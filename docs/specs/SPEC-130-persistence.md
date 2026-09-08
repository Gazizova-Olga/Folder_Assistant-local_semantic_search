# SPEC-130-persistence — Persistence

| | |
|---|---|
| Status | Draft — placeholder |
| Version | 0.1.0 |
| Owner | — |
| Last updated | 2026-09-08 |

## Purpose

Owns the folder-scoped database: its schema, its transactions, and the rules that keep concurrent readers and writers correct.

## Scope

**In scope**

- Schema and migrations.
- Transaction boundaries and connection policy.
- Repository contracts for manifest, chunks and vectors.

**Out of scope**

- What is embedded and how (SPEC-160).
- Conversation content (SPEC-100).

## Requirements

_Not yet written. This document is a placeholder created with the rest of the
specification baseline; it is filled in by the commit that builds the behaviour it
governs, and the version above moves when it is._

## Contracts

- Repository interfaces:
- Transaction boundaries:
- Error model:

## Data model

- Tables and keys:
- Indexes:
- Retention:

## Non-functional requirements

- Reliability:
- Performance:
- Operability:

## Open questions

## References

- [System concept](SPEC-000-system-concept.md)
