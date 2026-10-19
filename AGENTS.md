# Coding-agent instructions

Apply these principles within the authorized task and governing instructions; resolve conflicts by
instruction precedence and flag unresolved conflicts. Treat audit claims and decision records as
leads, not authority over current contracts.

Start with the [README](README.md) and [documentation map](docs/README.md) to find owner documents.
Read [Contributing](CONTRIBUTING.md) before edits; read [domain](docs/domain.md) and
[architecture](docs/architecture.md) before changing behavior, contracts, persistence or trust
boundaries. [Development](docs/development.md) owns commands and gates.

## Product boundaries

- `CaseFields` has exactly thirteen business fields. Revisions, operation IDs, instants,
  attribution, disposition and available commands remain outside it. Business-text acceptance
  and display safety are distinct; follow the Domain contract.
- Domain owns every transition and stewardship decision, including voided/erased refusal. Application owns operation
  identity, authorization policy and outcomes. Execution decides again against locked
  authoritative state; a preview, a view or an advertised command is advice. Adapters render
  outcomes and invent no rules.
- The ledger is the only record of what was accepted. Every accepted case operation and
  authority change is exactly one ledger entry, appended in the same transaction that updates the
  `cases`, `actors` and `grants` projections. Never write a projection alone, never read one as
  proof of acceptance, and never add a second store that must agree with the ledger at commit.
- A write is one `READ COMMITTED` transaction that first calls `claimcore.lock_ledger_head()`
  and only then reads authority, the operation ID and the case. Nothing that a decision depends
  on is read before the lock.
- The store locks, loads, calls one pure decision function and persists its result. It never
  decides. Domain, RecordFormat, Application and Wire perform no I/O and read no clock,
  time zone, environment or randomness; the instant and the payload nonce arrive in the locked
  view.
- An operation is its caller-chosen ID plus its RFC 8785 canonical request content. While request content
  remains retained, an authorized exact replay returns its original acceptance metadata with current
  snapshot disclosure; different content under a used ID is refused; a rejection records nothing.
  `OUTCOME_UNKNOWN` means a commit confirmation was lost and nothing else. Never infer that a
  commit failed, mint a replacement ID, or rebase a request.
- RecordFormat owns every byte-stable encoding and its canonical JSON writer. A format number
  identifies one byte grammar and its admissible value set; it changes when either changes. Format
  dispatch selects supported readers. Before 1.0.0, a release may
  explicitly refuse older installations untouched. From 1.0.0, keep reading every format a release
  wrote and ship migrations instead of refusing installations. The entry hash covers the salted
  payload commitment and never the payload, so erasure
  leaves the chain verifiable; do not add content-derived data to the hash preimage or to any
  column that erasure does not clear.
- Format invariants are checked on admission, decode and verification; SQL mirrors only these.
  Current text/date admission policy judges newly asserted values only, never accepted history.
- `VOID`, `REINSTATE` and `ERASE` are stewardship acts on a case record, not business commands.
  Erasure removes the business payload of a case and its whole history from the live database
  and retains the reference, revision, and ledger metadata. It does not reach WAL or backups,
  and the documentation must keep saying so.
- Actor grants are default-deny, scoped to the installation or one case, and owned by ClaimCore,
  not the identity provider. They are evaluated under the write lock or in the same snapshot as
  the read. An inaccessible case or operation has the same public refusal as an absent one. The
  role-to-capability and act-to-capability tables live in one F# module; SQL never encodes them.
  Current snapshots require case read; superseded snapshots require full history in the same
  locked/read context, including replay, execution, preview and observation.
- `ClaimCore.Hosting` is the only case-work composition root. Only Hosting and Database
  reference `ClaimCore.Postgres`; only Web references Hosting. Web uses Hosting/Wire/HostSecurity;
  CLI uses Wire/HostSecurity. `ClaimCore.Wire` references no ClaimCore project and owns the one
  typed codec from which the published OpenAPI contract is derived. The browser and CLI are
  authenticated HTTPS clients of one generated contract and never database peers.
  `ClaimCore.Database` is owner administration and serves no case work.
- The installation stores one immutable canonical IANA business time zone. Business dates derive
  from the database instant read under the lock and that zone, never from `DateTime.Today`,
  `TimeZoneInfo.Local` or a host default.
- The schema is an ordered, forward-only list of migrations in `db/migrations/`. A change ships
  as a new numbered script with its readers, writers, tests and documentation. Never edit a migration
  shipped by a release. Before 1.0.0, a release may instead replace the baseline and refuse older
  installations untouched. A database whose history is not exactly a prefix of that list is refused
  untouched. Supported PostgreSQL majors and minimum minors live in `db/postgresql-baseline.json`;
  qualify every listed major against its pinned image.
- The trust model and the non-goals in [operations](docs/operations.md) are part of the design.
  Do not add machinery for backup, restore, replication, handoff, copy custody, multi-party
  approval or a data-readiness mode without an owner decision that changes that document first.

## Safety and evidence

- Source review and green CI never grant owner approval. Publish a branch and pull request and
  report its exact revisions; do not approve, merge, enable auto-merge or change repository
  protections unless separately and explicitly authorized.
- Test only with synthetic data in isolated databases. Preserve adopted cases, database volumes
  and private local state. Never print secrets, connection strings or claimant payloads.
- Contract headings carry a unique, well-formed `CC-…` token in their owner document.
  Documentation checks validate token format, uniqueness and current relative links.
  Review a contract by reading its owner heading and the behavior tests relevant to the change.
- Tests run with `dotnet test` on the Microsoft Testing Platform. Every test module carries
  `[<Tests>]`; there is no hand-kept registry. Focused, pending, skipped, filtered or retried
  runs cannot satisfy verification. `bash eng/verify.sh` is the complete local gate and CI calls the same stages.
  PR browser smoke is explicitly partial; main/tag and explicit dispatch retain full qualification.
- Contract artifacts (`openapi.json` and its derived client types, descriptors and vectors) are
  generated and tracked in `contracts/`; the fingerprint is SHA-256 of `openapi.json` and covers
  the wire shape only. Run
  `dotnet run --project eng/ClaimCore.ContractGenerator -- --output contracts` and review
  the generated diff with the F# change. Freshness checks require exact inventory and bytes.
  Within-toolchain outputs stay ignored; web commands never invoke .NET.
- Suppress a lint, type or format rule only inline, for one named rule, with the reason on the
  same line. Function, lambda and member length and complexity limits apply to production and
  test code without exception. There is no file, module or value length limit: organize by
  capability, and split a file when it holds two responsibilities, not when it reaches a size.
- Ordinary .NET builds do not run npm. Report commands actually executed and their outcomes
  separately from source inspection. Keep generated reports and private local state in ignored
  paths, never tracked source.

- Private inputs are absolute, regular files with no other-access bits and no group write or
  execute bits, read from the admitted handle; there is no native code in the product or its tests.

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
