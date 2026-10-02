# Coding-agent instructions

Apply these principles within the authorized task and governing instructions; resolve conflicts by instruction precedence and flag unresolved conflicts.

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
- Keep contract headings in their registered owner documents and name at least one test per contract with
  its `[CC-…]` token; `ClaimCore.Docs check` fails on an unnamed contract and on a token that names none.
  There are no review hashes to refresh: review a contract by reading its heading and the tests that name it.
- Required .NET tests use native .NET 10 Microsoft Testing Platform commands through
  `node eng/ci/suites/suite.mjs`. A suite's expected tests are the lines of its generated
  `tests/inventory/<assembly>.txt` (rewrite with `node eng/ci/suites/inventory.mjs --write`, never by hand,
  and commit it with the test change); the registry `config/test-suites.json` holds no counts. Focused,
  pending, skipped, expected-failure, conditional, filtered, retried, or unregistered sharded runs cannot
  satisfy complete verification; the PostgreSQL integration partitions registered there are the only sharding.
- Contract artifacts are generated, not tracked. `npm --prefix web run contract:generate` must match
  `config/contracts.lock.json`; accept an intentional contract change only with `contract:lock` and review the
  lock diff with the F# change.
- Enforce size, complexity, and lint limits across production and test code without grandfathering.
  [`config/lint-exceptions.json`](config/lint-exceptions.json) is the sole registry of every lint, type,
  format and coverage exception for every language (F#, TypeScript, JavaScript, CSS, Python,
  shell, YAML, MSBuild). Follow [repository quality](docs/development.md#repository-quality):
  each entry has a stable `LX-nnnn` id, tool, exact rules, one file, kind (`inline` or `config`), an exact
  occurrence count, a substantive reason, an owner and an ISO review or expiry date. An inline
  suppression carries `lint-exception: LX-nnnn` in its own comment or the line above; a config-level
  ignore (ignore patterns, per-file ignores, disabled rules, `NoWarn`) is matched by file, tool and target.
  `node eng/lint/check-exceptions.mjs` fails on any unregistered, stale, blanket or miscounted exception.
- Ordinary .NET builds do not run npm. Produce Web assets with the locked frontend and publish only
  bytes matching its source, lock, contract, and toolchain manifest.
- Report commands actually executed and their outcomes separately from source inspection. Keep
  generated reports and private local state in ignored paths, never tracked source.

## Name by meaning, not incidental development history

Name files, directories and identifiers for their responsibility, behavior or domain concept, using the project's idioms. Keep terminology consistent within a context and make necessary distinctions between contexts explicit.

Prefer names that convey meaning in context. Broad names such as `utils`, `manager` or `data` deserve scrutiny when they hide responsibility or collect unrelated concerns. Clarify responsibility before renaming; refactor only when warranted and within scope.

Do not name things merely for implementation generations, task provenance, development status or unsupported superiority. `new`, `legacy`, `v2`, `temp` and `improved` are examples to examine, not banned words. Distinguish alternatives by meaningful properties: `StreamingParser` and `BufferedParser`, not `Parser` and `ImprovedParser`.

States, stages, ordering, lifetimes and versions are valid when they describe the domain, algorithm, contract, compatibility boundary or artifact. `api/v2`, `LegacyEncoding`, `TemporaryDirectory` and `FinalInvoice` can be precise. A versioned public contract need not lose its versioned name when older versions retire.

Apply this to comments, documentation, logs, errors and test descriptions too. Retain history where it explains current constraints or belongs in a historical record.

Review names introduced or affected by the task, including your prose. Fix misleading names within scope and update references. Respect external conventions, persisted formats and established compatibility commitments; use an authorized migration or deprecation path where needed.

## Simplicity

Build the simplest design that fully solves the stated problem and is easy to understand and change. Minimize concepts, special cases and hidden state, not lines of code. Keep complexity required for correctness, security, error handling and stated requirements. Flag avoidable complexity in explicit requirements without silently overriding them.

**Understand first.** Read relevant code, contracts and tests; validate assumptions before changing them. Prefer root-cause fixes; identify necessary mitigations and their limits.

**Look wider than the matter at hand.** Examine related defects, duplicated mechanisms and downstream effects for shared causes. Consider fundamentally different approaches, including replacement, where simpler. Keep removals and refactors within scope; report related work outside it with a recommendation.

**Before adding anything** (code, file, abstraction, option, dependency, check, document):

- Name the concrete need: a requirement, defect, maintenance burden or real risk. Without one, do not add it. Tie verification and documentation to these needs.
- Do not build speculative options, extension points or compatibility layers. Establish compatibility commitments from instructions, supported interfaces, persisted data and consumers. Coordinate changes with consumers and rollout; break commitments only when authorized.
- Prefer removing unnecessary work, suitable platform features, existing project code, then a maintained dependency or new code according to total complexity. Do not force reuse or a dependency that fits poorly.
- Keep one authoritative source per fact; derive or check dependent copies. Avoid hand-maintained derived inventories, not necessary source data. Keep test oracles independent of the logic under test.
- Avoid needless duplicate mechanisms; share abstractions only when they reduce total complexity. Do not merge distinct contracts merely because their code looks similar.

**Before removing anything:**

- Understand its users and the property it protects. If unsure, investigate; absence of search hits or test coverage is not sufficient evidence for deletion.
- Preserve required safety, security and correctness properties when replacing checks. Remove unnecessary mechanisms rather than adding tests or documentation to justify them.
- Delete what the task demonstrably makes obsolete, including your own superseded scaffolding.

**Judge the whole, not the part.** Improving a metric alone does not establish simplification; neither does shifting complexity to callers or users. Count concepts, moving parts, state and indirection across the system.

**Before finishing:** remove needless changes, then verify intended behavior and protection against regressions. Explain necessary complexity.

## Design before building

Scale design and review to risk, not line count: a one-line change to authorization or data deletion can be high-risk. Routine low-risk edits need only a brief sanity check; use distinct design and challenge passes for non-trivial changes to behavior, contracts or mechanisms.

1. **Design.** Establish the problem, relevant constraints, alternatives and chosen approach. Consider a fundamentally different approach where it could be simpler, and effects on callers, data, tests, documentation and operations.
2. **Challenge it.** Review the design in a separate skeptical pass. Test assumptions against code, experiments or counterexamples; try to make it fail. Revise where the evidence disagrees, rather than restating the design.
3. **Build.** Implement the reviewed design. If implementation invalidates an assumption, revisit and challenge that part before proceeding.

## Finish the whole change

Follow the change through its dependents, not just the edited files.

- Find affected callers, configuration, lockfiles, generated files, build/CI rules, documentation, tests and external consumers. Update in-scope dependents; identify needed external coordination.
- Search relevant inputs, including hidden, ignored or binary files when needed. Check tool exclusions and stale references; retain valid history, compatibility and rejection-test references. No text matches does not prove completeness.
- Check that intended files reach deliverables and relevant files reach required checks, including test discovery. Inspect tracking, ignore and packaging rules; regenerate affected outputs per project policy.

Report implementation, verification and external rollout separately. Do not claim an unmet requirement is satisfied or widen scope without authorization.

## Checks must prove something

A check, test, gate or record must support a property someone relies on; match the strength of the claim to the evidence.

- Hashes can verify identity or integrity against a reference, not review quality. A completion claim does not establish execution; mocks alone do not demonstrate real-system integration. Use real-boundary verification where the property requires it.
- Test intended behavior, boundaries and relevant failures. Give guards a case they must reject. Use regression tests or negative controls to demonstrate detection, not just code execution.
- Keep controls proportionate. Do not build a check mainly to appease another check or inflate coverage; address the underlying risk. Independent checks of the same property can be justified.
- Fix real findings; correct invalid checks against established requirements. Do not lower required assurance without authorization. Exceptions must be explicit, narrow and justified.
- Investigate relevant failures, including pre-existing ones. Fix in-scope issues, continue independent safe work, and report unresolved failures or unavailable checks. Never report a failing or unrun gate as passing.

## Verification

An exit code establishes only what that command's success criteria mean. Check the intended result; local success does not establish success in the authoritative environment.

- After a meaningful state-changing operation or related batch, inspect its effects: changed files, commit contents, generated outputs or installed versions. Preserve failure visibility; do not let a later success mask an earlier failure.
- Check expected outputs and test discovery, including skipped tests and exclusions. Investigate unexpectedly empty, small or missing results before treating them as success; expected quiet output is not itself a failure.
- Diagnose from the full relevant failure output, retaining details without dumping sensitive or excessive content.
- Use targeted checks during iteration; run required verification where authorized and available. Prefer an isolated, clean environment matching the required platform, toolchain and restore mode without discarding user work.
- Verify the delivered state, not an earlier working tree. Report what ran, outcomes and untested platforms or conditions.

## Documentation and comments state the current truth

Describe supported behavior, assumptions and reasons. Keep history in change or decision records unless it explains a current constraint or migration; do not erase useful rationale. Prefer links, generated content or consistency checks over manually duplicated reference facts. The authoritative source may be code or configuration, not a document. Update affected documentation, examples and error text with the change, and check affected links and generated blocks proportionately. Do not create documentation machinery merely to satisfy this rule.

## Working hygiene

- Check inputs and prerequisites cheaply before long runs.
- Keep running checks' inputs stable or isolate them; intended watch/reload workflows are fine.
- Preserve unrelated work. Clean up your temporary resources, not deliverables or useful failure evidence. Leave requested services running.
- Keep output focused; do not dump large generated files or expose secrets.

## Easy to start

Document the shortest setup from a clean checkout, prerequisites and actionable errors. Reuse commands; do not mandate setup wrappers or doctor tools. Declare each toolchain version authoritatively; derive or check required copies. Pin inputs for reproducible builds; retain supported dependency ranges and deliberate compatibility probes.

## Changelog: record release outcomes, not development history

Record notable net changes for users, operators, integrators and contributors against the relevant published baseline. Lead with what changed and who is affected; state breaking changes and necessary action, including breaking fixes. Substantiate claims and retain material commands, public identifiers, limits and non-guarantees. Link detailed explanations rather than narrating implementation; do not invent benefits or present unfinished capabilities as delivered.

Consolidate related pending work into its final outcome. Omit routine churn and wholly reversed, unpublished work with no remaining consequence. Classify against released behaviour, not commit labels. Fold repairs to never-released functionality into the completed feature; retain meaningful consequences for public-prerelease users.

Use established categories consistently and omit empty ones. Internal is for noteworthy implementation, verification or release-process outcomes, not routine churn or concealed compatibility changes, new build requirements or user-visible effects. Keep technical method and protocol identifiers; omit development-phase labels.

Retain Unreleased and release history. Preserve version attribution, dates, links and publication-status distinctions. Published entries may be clarified, consolidated or reclassified without changing what they say shipped; factual corrections require evidence. Do not prune or relocate history without an explicit policy or request, or alter tags or published artifacts when editing the working file.
