# Coding-agent instructions

Start with the [README](README.md) and [documentation map](docs/README.md) to find owner documents.
Read [Contributing](CONTRIBUTING.md) before edits; read [domain](docs/domain.md) and
[architecture](docs/architecture.md) before changing behavior, contracts, persistence, recovery, or
trust boundaries. [Development](docs/development.md) owns commands and gates.

## Product boundaries

- `CaseFields` has exactly thirteen business fields. Revisions, operation IDs, timestamps,
  attribution, and available commands remain outside it.
- Domain and Application own business rules, transitions, available commands, outcomes, actor
  authorization, and recovery decisions. Normal service callers use actor-bound `IActorClaimsCore`;
  recovery, lifecycle, tombstone and management work stays behind its typed members. Web binds an
  OIDC principal to that facade and renders its outcomes; the browser and CLI are authenticated
  HTTPS clients, not database peers. They do not invent rules or receive stores, retained
  preparations, recovery ports, witness credentials, or transition callbacks.
- `ClaimCore.Hosting` is the only case-work runtime composition root. A case-work host never links
  `ClaimCore.Postgres` or reaches schema administration. `ClaimCore.CliProtocol` owns CLI-v4
  framing, OIDC delivery, and response classification over the generated service contract;
  `ClaimCore.Cli` is only the process entry point. `ClaimCore.Database` is the separate owner-only
  administration surface, not a CLI or Web backdoor.
- RecordFormat owns canonical operation and snapshot encoding. An uncertain retry must preserve the
  exact operation ID and request bytes; never rebase it or infer that a commit failed.
- Operation identity, authority, and knowledge are separate. Accepted history proves acceptance;
  durable revocation ends future unaccepted authority without rewriting old attempt uncertainty.
  Recovery list defaults to pending work, detailed attempts are operation-bound and paged, and a
  pruned revoked preparation may expose only its tombstone.
- Native `IClaimsCore.Prepare` and `Execute` accept Domain `CommandRequest`. CLI and Web bind their
  generated form shape once through `Drafts.bind`; do not add another public raw-field boundary.
- The separate witness owns ordered authority evidence and exact settlement/readback; primary
  rows alone do not prove freshness or a definite accepted outcome. Actor grants are default-deny,
  rechecked under authoritative locks, and cannot be inferred from a database role. An inaccessible
  case or operation has the same public refusal as an absent one. Case-list cursors are opaque,
  principal/grant/query-bound and short-lived.
- A complete data audit first drains authority operations through post-COMMIT witness settlement
  under an exclusive cross-process session lease, then takes the primary authority lock. During ordinary
  writer activity, holds the independent witness read fence through one stable snapshot. Pending
  handoff, activation and loss phases already fence ordinary witness appends; owner audit holds
  the primary lock and refuses witness-tip movement. Failed or overdue scheduled audits close
  actor-bound access. Terminal installation-loss retirement is owner-only, signed by two
  distinct current human owners, and never turns missing history into a restored case or permits
  old recovery identities to resume.
- A data-entry-error void preserves history and is not erasure. Live purge, witness-payload prune,
  managed-copy deletion, and terminal privacy certification are distinct owner/evidence-bound
  steps. Active holds, unknown copies, unsettled attempts or missing independent verification keep
  the state pending; keyed suppression evidence remains pseudonymous data.
- `CORRECT_CASE` has three explicit tagged groups. Keep reads current accepted state; never add a
  business field, broaden historical `AMEND_REGISTRATION`, or accept an adapter-only correction rule.
- The fresh initializer atomically stores the installation's immutable canonical IANA business time zone. Runtime
  opening requires it; derive business dates from one captured instant and that stored zone, never
  `DateTime.Today`, `TimeZoneInfo.Local`, or a host environment default.
- Storage has one checksum-bound fresh baseline, not an upgrade engine. Refuse unsupported old
  namespaces untouched. No reset, adoption, automatic deletion or recovery-format conversion.
  Update readers, writers, constraints, contracts, tests and documentation together.

## Safety and evidence

- Follow [Owner review](docs/owner-review.md). Source review and green CI never grant owner approval.
  Publish a branch/PR and report its exact revisions; do not approve, merge, enable auto-merge or
  change native protections unless separately and explicitly authorized. Do not use shared owner
  credentials as evidence of independent human review.
- Test only with synthetic data in isolated databases. Preserve adopted cases, database volumes,
  private `.local` state, and retained old installation evidence. Never print secrets, connection strings,
  recovery bytes, or claimant payloads.
- Keep contract headings in their registered owner documents and evidence-test leaf names prefixed
  with one matching `[CC-…]` token. Reattest `eng/ClaimCore.Docs/contract-reviews.json` only after
  reviewing the exact heading and assertion sources.
- Required .NET tests use native .NET 10 Microsoft Testing Platform commands and exact registered
  counts. Focused, pending, skipped, expected-failure, conditional, filtered, retried, or
  unregistered sharded runs cannot satisfy complete verification; the PostgreSQL integration partitions
  registered in `eng/test-partitions.json` merge into one exact report and are the only sharding.
- Enforce size, complexity, and lint limits across production and test code without grandfathering.
  [`config/analyzer-suppressions.json`](config/analyzer-suppressions.json) is the sole source-code exception
  registry.
  Follow [repository quality](docs/development.md#repository-quality): entries need an owner,
  exact file/rule/scope, substantive rationale, and an ISO review or expiry date. Directives need
  nearby rationale and a `suppression-registry: file|rule|scope` reference. Unregistered bypasses
  fail.
- Ordinary .NET builds do not run npm. Produce Web assets with the locked frontend and publish only
  bytes matching its source, lock, contract, and toolchain manifest.
- Report commands actually executed and their outcomes separately from source inspection. Keep
  generated reports and private local state in ignored paths, never tracked source.

## Name by meaning, not development history

Name files, directories, and all identifiers—including functions, types, variables, tests, and configuration keys—for what they actually represent or do. Use precise, consistent domain terminology and idiomatic project conventions. Do not encode product maturity, implementation-plan stages, task provenance, temporary development status, or replacement history. Distinguish alternatives by meaningful differences, not vague labels or unsupported quality claims.
Apply this semantically, not as a word blacklist. States, stages, versions, and ordering are valid when intrinsic to the domain, algorithm, contract, or artifact. Planning and historical records may identify the work they document.
Review names you introduce or change. Keep renames within scope, update affected references, and honor external naming and compatibility requirements.
