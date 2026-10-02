# Resource ownership and asynchronous lifetimes

## Findings

- RuntimeResources constructs three pools and cursor custody before its caller can own the object.
  Later construction failure can strand earlier acquisitions; sequential cleanup can also stop
  before closing all pools. RuntimeOpening creates a witness store before fallible custody loading,
  leaving its capability and read-fence pool unowned if loading fails.
- Runtime starts its audit worker before the final CurrentUseState database read. Failed adoption
  closes the pools but cannot find and stop that worker.
- Deferred RuntimeAdmission cleanup stores a raw provider exception in a faulted completion task.
  A disposer that already timed out may never observe it. Keep completion and bounded failure
  knowledge without retaining an unobserved provider exception or changing admitted outcomes.
- Web configuration can acquire an issuer certificate before later validation fails. Program
  verifies metadata before owning certificates, and a built application is not explicitly scoped
  across failures before Run. Construction, startup and normal shutdown need clear owners.
- A real CLI session probe held stdin open, sent SIGINT, and observed no exit until EOF; only then
  did it return 130. Synchronous framing cannot observe the cancellation token while blocked.
- Backup control uses a per-read socket timeout for byte-at-a-time framing. Continued trickled
  bytes can extend the wait beyond the capture budget while holding authority resources.

## Chosen design

Use a small internal Hosting construction scope to register owned disposable resources until they
are transferred together to their completed parent. Unwind every registered resource on failure
with the existing bounded cleanup classification. Use it for runtime pools/cursor custody and
witness store/custody acquisition. Do not add a general public ownership framework or dependency.
Perform fallible runtime readiness reads before starting the audit worker.

Represent deferred cleanup completion as a success/failure value. Subsequent disposal reports the
existing bounded failure; the last admitted lease still starts cleanup without replacing its
operation result. Preserve close-before-stop-before-drain and joining audit/cancellation work.

Scope Web certificate acquisition through all configuration failures, own certificates before
startup verification, and scope the built application across route setup and execution. Separate
certificate loading/validation as a cohesive responsibility; keep trust policy and diagnostics.

Let CLI framing own a processor and its interruption knowledge. Protect interruption and dispatch
admission with one gate: interruption before possible mutation dispatch yields 130; interruption
after possible mutation dispatch yields 4. The process entry point handles SIGINT and terminates
with that decision, closing its process-owned blocking streams without abandoned reader tasks.
Retain existing framing helpers for their direct callers. Do not infer failed commit or invent an
operation identity, and do not kill the browser handed to the operating system.

Read backup frames asynchronously with a linked token and one frame deadline derived from the
stream's configured timeout. Await the whole read at the synchronous administration boundary.
Pass the existing capture token to subsequent reads; keep the initial and observation budgets,
byte limit, protocol and uncertain completion semantics. Unwind socket construction failures.

## Verification and dependents

Use construction failure counters, actual witness-store closure, deferred cleanup failure, native
certificate lifetime checks, real sockets with trickled input, and real CLI processes interrupted
before EOF. Give interruption a pre-dispatch race and a possible-mutation negative control.
Regenerate affected inventories, update owning contracts/docs and Unreleased outcomes, then run
complete affected suites, repository quality, published clients and exact-head CI before the
explicitly requested merge. Keep all data synthetic and reports ignored; preserve private state.
