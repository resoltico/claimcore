# Test evidence design challenge

This separate pass challenges the proposed changes against domain rules and decoder control flow.

- A second oracle must not call Claim.availableCommands, Eligibility or validation to decide its
  expected result. Its intentionally finite fixture vocabulary permits literal canonical amounts
  and dates. Production Claim.view is only the observed result. Retain exact expected state across
  refusals, and distinguish a valid advertised correction from a repeated no-op correction.
- Correction combinations can be structurally valid requests without being executable in every
  state. Record decoding tests should cover their representation without treating a round trip as
  domain admission. Keep the existing exhaustive eight-state correction matrix for execution.
- Guid.Empty and unchanged command choices would invalidate identity sensitivity controls. Use a
  fixed distinct replacement operation ID and an OPEN/CLOSE alternative guaranteed to differ;
  keep generated revisions below overflow and references below the text limit.
- Snapshot seeds need a snapshot encoding, not command bytes. Recovery seeds need the matching
  installation, epoch, time and synthetic keys. Require exact accepted seed meaning so a refusal-only
  decoder cannot silently pass totality. Raw mutations can reach outer parsing and authentication;
  they do not establish authenticated arbitrary-plaintext coverage.
- Negative controls must use the same comparison exercised by the real sequence, with deliberately
  wrong observable outcomes. A separate assertion that merely says false would prove nothing.
- The Stryker configuration already names metadata, initial state, request freezing and reducer.
  The apparent documentation mismatch was disproved by reading current configuration. Keep it.
- Existing inventory/report policy tests reject missing, duplicate, skipped and substituted evidence.
  Fixed encoding vectors, correction matrix, real PostgreSQL tests and published browser/CLI tests
  supply distinct evidence. Do not duplicate their mechanisms or claim the proposed unit changes
  prove integration, production performance, or every possible defect.

Accepted with these constraints. Implement test-only changes, preserve payload-safe diagnostics,
update the narrow evidence description, and verify full discovered suites and authoritative CI.

The follow-up distinguishes representation from admission: all-KEEP must decode but preparation
must return CorrectionNoChanges. Do not exclude it silently from the vocabulary. Golden vectors
must start encoding from test-owned typed expectations, not decoded production values; decoding
must match those same independently stated fields. Their existing fixed digests remain unchanged.
This strengthens the oracle without replacing the fixed bytes or deriving expected hashes at run time.

Final challenge: a missing-group refusal must receive syntactically valid JSON, and vector family
coverage must compare actual names with the independent expected set, not only count entries.
Round trips can also hide paired correction-mode swaps; inspect encoded modes against literal
KEEP/REPLACE/CLEAR expectations for every generated combination. Use boolean structural comparisons
for the new snapshot seed assertion so failing diagnostics cannot print claimant fields.

The recheck follow-up was reproduced: selecting a registered unit-only property in the fuzz suite
produced seven passed tests without running any fuzz property. Guard the effective environment,
including registered suite overrides, before building or creating a results directory. Reject any
nonempty recheck input as diagnostic configuration; retain normal required and extended profiles.
Keep token parsing in the F# harness rather than duplicating it in JavaScript. Prove wiring with a
real runner subprocess that refuses recheck mode before restore or evidence creation. Do not change
the native diagnostic replay capability or weaken inventory/report validation.

The limiter experiment passed all 138 Web tests with Program's core permits increased by one.
The overload assertion therefore protected a test-owned copy. Share the existing registration as
an internal Web module through the already declared WebTests friendship; add no friend or public API.
Preserve production rejection task semantics, no-store headers, policy names, defaults, queue order
and middleware position. Fixture configuration remains explicit and supplies independent expectations;
do not generate an extra certificate merely to obtain its limits. The OIDC fixture's declared queue
limit must agree with its registration. After sharing, inject the same off-by-one into the common
mechanism and require the existing overload test to fail, then restore and run full verification.
