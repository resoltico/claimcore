# Verification, delivery and operations design

This audit traces source, discovery, tests, measurements, artifact transfer and runtime ownership.
[Development](development.md) owns verification commands, [Architecture](architecture.md) the
component boundary, and [Operations](operations.md) deployment/backup admission.

## Concrete findings and decisions

- Web startup appends `/` to a slash-ended issuer, creating a different discovery path. Construct
  that path from the complete configured issuer with only the joining separator removed; retain exact
  issuer identity comparison. Test root and path issuers at the actual request boundary.
- `ResponseHeadersRead` ends HttpClient's timeout coverage at headers. Several discovery, token and
  service body reads lacked a complete request deadline, and buffering omitted its cancellation
  token. Use linked whole-request deadlines based on the existing client timeout, including bounded
  body buffering. Web startup keeps its ten-second limit; CLI clients keep twenty seconds. Transport
  timeout after mutation dispatch remains uncertain and never proves rollback or authorizes retry.
- Runtime closes admission only after waiting for scheduled-audit disposal. Move audit/source cleanup
  behind the admission owner's existing bounded drain. A disposal failure must not skip remaining
  pools, custody or cursor-key cleanup; attempt every owned resource and report a bounded failure.
  Never dispose resources beneath an active lease or rewrite an admitted outcome.
- A real probe showed that an empty XML report claiming 100% passes the coverage floor. The last
  qualified merged report included ContractGenerator tooling and omitted the CLI process assembly.
  Derive the production set from the architecture manifest, measure published CLI execution, filter
  tooling from aggregation, require every production package, and independently count merged class
  lines/branch conditions. Empty, duplicate and missing measurements and invalid counts fail. Reported and measured rates must independently meet the same floors. Keep all
  numeric floors and measured browser evidence requirements unchanged.
- Published test process capture bounds output only after `ReadToEndAsync`, while synchronous stdin
  can block before its timeout is reached. Use bounded concurrent input/output and a whole-process
  deadline with tree termination. Share only this test-process responsibility, not product decisions.
- The CLI applies the 128 KiB recovery/request allowance to all JSON responses. An actual encoded
  valid 50-entry full history with maximum-length Unicode fields is 201,201 bytes. Give service JSON
  a separate finite 16 MiB allowance, accommodating paged histories and bounded owner reviews; keep
  recovery artifact reads at 128 KiB. Test the actual codec, schema and response reader together.

## Boundaries retained

Native test discovery and exact TRX reconciliation already enforce the complete registered inventory
and disjoint integration partitions. Compiler/architecture controls have positive and negative
cases. Frontend mutation checks reject timeouts and ignored mutants, and production routes and
published HTTP/CLI/browser lifecycles exercise actual adapters. These stay mandatory.

Publish consumers verify the complete received tree before and after use. CI uses immutable
current-run/current-attempt artifacts and tests one publication across all consumers. Coverage
instrumentation must restore those exact bytes; post-use manifest verification remains mandatory.
Clean source copying detects changing inputs and excludes private state. Build pins and locked
restores remain authoritative. No new upgrade, compatibility, deployment or review-approval layer.

Backup/restore remains a separate independent evidence contract: captured is not verified, a primary
alone is not freshness, W1 alone is not activation, and synthetic same-host drills do not certify
independent custody. Retain exact labelled cleanup, fenced full audit, signed inventory/checkpoint,
WAL coverage, current human approvals, refusal and uncertainty distinctions. No readiness deadline
or failure assertion will be weakened to conceal an environmental failure.
