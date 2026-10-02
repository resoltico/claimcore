# Capacity, scheduling and performance design

## Evidence and requirements

Keep exact operation identity, current grant checks, the complete primary/witness audit fence and
ownership through post-COMMIT settlement. Throughput does not justify weakening those protections.
The register supplies no production throughput SLO or independent-host capacity certificate.

An isolated PostgreSQL 18.6 SQL-plan probe populated 100,000 synthetic case projections with ten
case-scoped grants. The current visible-list query scanned all case rows and repeated its grant join
100,000 times (about 47 ms on this machine). These projection-only probes test SQL work and access
filtering, not accepted-history or whole-runtime integrity. A separate witnessed workload must test
case work, history, recovery, export, audit and contention through actual boundaries.

History and audit case/history windows already have SQL limits. Retain their bounds and existing
case-reference/revision indexes. Full audit pages are streamed but must inspect all required evidence;
do not replace full reconciliation with sampling, a checksum claim, or an incremental trust cache.

## Chosen changes

1. Select list candidates from actor grants for case-scoped access. Keep installation-wide access
   on the ordered case-reference path. Make the paths mutually exclusive, deduplicate multiple roles
   for one case, and apply identity/enabled, role, active-grant, disposition, privacy and continuation
   predicates before limiting. Project ordinary case columns only after the narrow candidate page.
   Existing case-ID and reference indexes suffice; no fresh-baseline or wire change is required.
2. Schedule the next complete audit one interval after the preceding completion. PeriodicTimer's
   accumulated tick currently causes immediate repeated audits when an audit outlasts its interval,
   repeatedly fencing ordinary work. Keep the audit execution budget, monotonic health check and
   sticky failure/overdue quarantine. Never overlap or silently skip a required audit.
3. Close runtime admission, request audit cancellation, then drain admitted work. Cancellation
   currently starts only in final resource cleanup, after actor work drains. Request cancellation
   without synchronously waiting for its callbacks; cleanup owns joining the audit and cancellation
   callbacks before disposing their pools. Cancel only the audit, never an admitted case mutation.

## Alternatives and verification

Reject extra queue classes, new tuning options, additional connection pools, speculative indexes and
unqualified caching. The Web limiter already uses bounded oldest-first admission; audit and disclosure
fences have separate bounded pools to avoid nested exhaustion. Preserve those boundaries and test
their actual behavior, including queued overload refusal and canceled readers.

Use plan work counts and selective-access controls rather than fragile absolute-time assertions.
Exercise multiple history/audit pages and competing witnessed work with small pools, a held authority
fence and shutdown. Test long audit cadence, early stop, duplicate disposal and failure quarantine with
controlled signals. Register every new test in its owning suite and regenerate official inventories.
Record useful timings/counts in ignored output; report measured scope separately from source inspection.
Update current contracts/documentation and Unreleased outcomes, then run required gates and exact-head
CI before the explicitly requested merge.

## Overload execution knowledge

The real HTTP queue test proves WEB_BUSY is emitted before any core dispatch, yet the shared host
contract currently declares a null execution phase. Declare NOT_STARTED for this typed refusal so
browser mutations retain definite non-admission knowledge. All current Busy producers are admission
limiters, including login; no admitted operation uses this cause. Update generated host schemas,
validators, TypeScript types and corpus through the official contract lock, and qualify browser
classification. This intentional wire refinement requires matching host/client artifacts.
