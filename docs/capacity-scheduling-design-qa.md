# Capacity and scheduling design challenge

This separate pass challenges the design against current SQL, lifetime ownership and audit rules.

- **Do not infer unbounded history from a reader loop.** Sql.history already limits the window to
  MaximumPageSize plus one, and audit replay uses bounded pages. The initial apparent history defect
  was disproved by reading its shared SQL. Keep those mechanisms rather than adding redundant limits.
- **Sparse access is different from installation access.** The proposed 100,000-row scoped-grant
  probe performed ten case-ID lookups and ten reference lookups (83 shared hits, about 0.21 ms here),
  instead of a complete case scan. Qualify the installation path separately, including continuation,
  duplicate roles, inactive/foreign actors, hidden disposition/privacy states and no grants.
  A faster projection-only SQL plan does not prove witnessed case-work or audit capacity.
- **Grant predicates remain authoritative.** Both branches must use the same current authenticated
  actor and role set under the primary authority lock. Their mutually exclusive predicates must
  prevent installation-plus-case grants duplicating a row. Do not change cursor binding or evidence
  verification, and do not select inaccessible rows before the page limit.
- **Audit fences are required complexity.** A complete audit still drains post-COMMIT settlement and
  holds one stable primary/witness cutoff. Do not relax locks to improve timings without establishing
  all writer and disclosure dependencies. Explicitly measure competing work and report maintenance
  contention rather than calling same-host throughput a production guarantee.
- **Cadence is completion based, not a catch-up queue.** A long audit must leave one interval before
  the next starts. The last-completion monotonic health bound remains unchanged, and an overdue or
  failed run stays quarantined. Shutdown cancellation must not turn a failing active installation
  into a healthy reopened runtime.
- **Stop must precede drain.** New actor admission closes before requesting audit stop. Use platform
  asynchronous cancellation so callbacks cannot consume the actor drain budget. The cleanup owner
  must observe callback failures, join worker/callback tasks and try every resource. Repeated stop
  and concurrent disposal must not cancel an admitted mutation or dispose its pools early.
- **Tests must reject the old behavior.** Prove sparse query work with EXPLAIN ANALYZE and independent
  expected rows; let the old query fail the work bound. Hold an audit past several intervals and
  ensure another run does not start immediately. Hold an actor lease while requesting shutdown and
  ensure audit stop is observed before that lease ends, yet its result remains unchanged.

Accepted: the existing indexes support the query change; no new schema, dependency, tuning option,
pool or service protocol is needed. Full reconciliation's linear evidence work and write pause remain
necessary limits, with bounded client admission and fail-closed health checks.

The overload follow-up is accepted after the HTTP test reached exactly four held core calls and a
typed WEB_BUSY refusal with null phase. Program and test-host concurrency/login limiters are its
current producers, all before dispatch. Change only Busy, not unrelated host failures or already
admitted outcomes. A refused new request does not settle an older uncertain operation; preserve the
existing exact-ID recovery rules. Review the intentional generated lock diff and rebuild clients
with the new Web fingerprint; do not claim backward wire compatibility for the old null-phase body.
