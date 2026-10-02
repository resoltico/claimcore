# Verification and delivery design QA

This separate pass challenges the [design](verification-delivery-design.md) before implementation.

1. **Headers are not a complete request.** Suspend an HTTP content stream after successful headers;
   neither client timeout nor frame timeout alone currently guarantees body completion. Carry one
   explicit deadline through body reads and test cancellation there, with healthy counterparts.
2. **Client cancellation is not server rollback.** Preserve the existing dispatch marker and closed
   delivery classification. Read failure is definite; dispatched mutation failure remains unconfirmed.
   Do not introduce a cancellable mutation execution on the server.
3. **Shutdown order can create a window.** Audit waiting before admission closes allows new actor
   work while disposal is already requested. Cleanup belongs to the admission owner after draining;
   an uncooperative audit cannot cause resources to close under active leases. Use the existing
   bounded disposal behavior and prove both drain and refusal of new admission.
4. **Cleanup must survive earlier failures.** Inject several independent throwing disposables and
   verify every resource is attempted, with bounded nonreflecting failure. Preserve existing adopted
   resources and known commit outcomes. Witness store/key cleanup needs the same protection.
5. **Raw and merged coverage formats differ.** Actual Coverlet branch counters can include compiler
   branches absent from its per-line projection. Do not falsely demand equality there. Independently
   verify the merged class-line projection independently. An actual re-merge retained some Web
   counters not represented by its class lines, disproving strict equality even in merged output.
   Require both the existing advertised floors and the same independently measured floors; neither
   may compensate for the other. Reject missing/empty/duplicate measurements and invalid counts.
   Retain measured browser/CLI branch checks on raw inputs and test actual exports too.
6. **Tooling can inflate a production metric.** Derive the exact production assembly names from the
   component manifest; exclude tooling during aggregation and reject missing or unexpected merged
   packages. Measure the actual published CLI instead of inventing an exception for its entry point.
   Keep source/toolchain/artifact and before/after byte checks intact under instrumentation.
7. **A process bound checked after allocation is not a bound.** Child stdout/stderr and stdin must
   progress concurrently; over-limit output or a blocked pipe must trigger the same deadline/kill
   discipline. Test real child processes, including finite output, oversized output and blocked input.
   A real CLI discovery probe emitted about 5.9 MB; its formerly unbounded unit capture therefore
   uses a finite 16 MiB budget, while the existing published-acceptance budget stays 1 MiB.
8. **Green evidence is not approval or production certification.** Qualification uses synthetic
   isolated databases and exact revisions; source inspection, local containers and artifact hashes
   cannot establish independent human approval, lost history or erasure of unknown copies.
9. **Calendar-dependent fixtures can test the wrong refusal.** The complete integration run found
   a capacity test proposing a hold whose fixed review date had expired. Give the new proposal a
   future review date so it reaches capacity validation. Keep the historical seeded holds, which
   remain active after their review date, and the assertion that refusal appends no witness intent.

This pass selects existing platform cancellation and lifetime mechanisms. It does not add speculative
options, a new business facade, another deployment composition root or another test runner.
