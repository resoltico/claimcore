# One authority, one ledger

Decision made 2026-10-13. [Decision 0002](0002-canonical-records-and-presets.md) refines record
formats and role presets. These decisions describe supported design, not execution certification.

## Decision and guarantees

ClaimCore is a case register over one PostgreSQL authority. Domain owns business and stewardship
transitions; Application owns identity, authorization and outcomes; the store locks, loads, calls
one pure decision and persists its result. Browser and CLI are HTTPS clients of one generated
contract. Database is a separate owner executable and serves no case work.

Every accepted case act and authority change appends one hash-linked ledger entry in the same
transaction as projections. Writers lock the singleton head before any decision input and then
capture database time. Reads use one REPEATABLE READ snapshot. The persisted canonical IANA zone
determines business dates. Preview and advertised actions are advice; execution decides again.

Exact operation identity and canonical content replay returns the original receipt, subject to
current authorization and snapshot disclosure. Rejection records nothing. Lost commit confirmation
is unknown; preserve the exact ID/content/revision and retry without rebasing. No unaccepted request
is retained by the service. Ledger hashes cover metadata and a salted payload commitment.

VOID, REINSTATE and ERASE are case-record stewardship acts. One current human steward may erase at
the expected revision. Erasure clears live case payload/nonce and business projections, reserving
reference/revision and preserving chain metadata. It cannot erase dead row versions, WAL or copies.
An earlier backup can resurrect payload; independently retained post-erasure evidence detects its
missing history but cannot prove that no newer acceptance exists.

## Trust and non-goals

The service host, database owner/superuser, IdP and backup holders remain trusted. Integrity against
retained checkpoints does not defeat an administrator rewriting all evidence, prove freshness, or
prove real-world assertions. The payload-clear guard exception is owner-trusted; verification detects
clearing without ERASE. No backup/restore/replication orchestration, custody, handoff, multi-party
approval, readiness mode or request-recovery store is supplied. No historical installation conversion
is supplied. Operators own retention, independent evidence and deployment qualification.

## Alternatives and challenge

Retaining a witness/custody subsystem would preserve a two-store commit protocol and operational
burden. Stateless preview plus idempotent execution gives review/retry without server preparation
state. Co-committed projections provide direct reads; replay-only indexed reads would depend on
historical authorization policies. Verification compares projections to accepted history instead.

PostgreSQL evaluates a locking SELECT target-list clock before its lock wait. The implementation
therefore calls `lock_ledger_head()` before a separate database clock read; lock/statement/idle
transaction timeouts bound serialization. Unsalted content digests cannot survive erasure. Logs use
closed metadata fields and exclude provider messages and claimant content. Durable format numbers
follow their bytes; wire compatibility follows one acyclic fingerprint. Compiled boundary checks
have rejecting controls; automatic discovery protects test membership without a hand-kept registry.

## Consequences

A single trusted database gives atomic acceptance and honest uncertainty with fewer moving parts.
Operators retain checkpoints and test restore/erasure procedures externally. Default-deny roles and
current disclosure govern every access. Development installation/client formats are intentionally
incompatible; preserve unsupported databases with matching software and initialize separately.
The owner reviews and merges; source review or green CI grants no approval or production assurance.
