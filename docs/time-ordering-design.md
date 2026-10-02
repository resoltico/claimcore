# Time, ordering and expiration

## Findings

Browser ticket idle/absolute lifetimes and CLI token caching depend only on wall time. Backward
adjustments can extend reuse. CLI token lifetime also starts after delivery and parsing, granting
network delay extra validity and cache life. Initial validity and the reuse margin are distinct.

Lifecycle and tombstone workflows pass an Application business-clock instant into persistence
before waiting for authority/case locks. Approval eligibility, hold review windows and event times
can therefore describe an earlier request rather than the locked decision. Signer approval issuance,
managed-copy retention transitions and recovery paging also consult the host clock instead of the
primary clock used by their adjacent durable authority paths. Case-list paging captures its business
clock separately for validation and continuation issuance.

## Chosen design

Use the platform TimeProvider for process-local lifetimes. Tickets retain their original bounded UTC
deadline and gain elapsed absolute/idle limits sampled under the ticket gate. Renewal cannot extend
original absolute authority. Tokens preserve their public UTC expiry but cache validity additionally
uses elapsed time from the start of token HTTPS dispatch. Reuse retains the thirty-second margin;
a fresh shorter-lived token can be used once. A reply delivered after its actual lifetime cannot
authorize service dispatch. Do not impose a new minimum issuer lifetime merely for cache reuse.

Use one Postgres SQL clock reader for current primary UTC, with synchronous and asynchronous entry
points for the existing execution styles. Remove caller-supplied time from internal lifecycle and
tombstone ports and their Application wiring. Capture database time after authority/resource locks;
check fresh temporal eligibility before creating new authority, while retaining exact historical
replay independently of current expiry. Apply the same primary clock to signer issuance, copy
retention and list paging. Do not use transaction-start time for a decision delayed by locking.

Keep business calendar capture separate: one instant plus the persisted IANA zone, with unchanged
accepted-date reaffirmation after rollback. Add boundary controls against the actual Hosting capture.
Keep monotonic full-audit scheduling and revision/witness ordering; timestamps are not sequence IDs.
Current signed health remains strict about future evidence and exact expiry; settled historical
readback does not regain fresh authority merely by parsing an old certificate.

## Scope, alternatives and evidence

A global clock, durable wall-time high-water mark or replacement time-zone database would conflate
distinct requirements and add unsupported state. Database UTC is the authority clock, not proof of
elapsed time across a database clock correction. Independent signers/verifiers and serving hosts
still require synchronized UTC and consistent tzdata. Recovery/history cursors remain query positions,
not signed authority; current grants and disclosure checks remain mandatory.

Use independent fake wall/elapsed clocks for local lifetime boundaries and delayed token delivery.
Use real PostgreSQL lock delay and database-clock comparisons for approval eligibility, plus current
health expiry/future controls and real time-zone boundary vectors. Update internal callers, named
contracts, tests, generated inventories and outcome-focused Unreleased notes before final QA and CI.
No storage baseline, signed encoding, client payload, migration or release version changes are planned.
