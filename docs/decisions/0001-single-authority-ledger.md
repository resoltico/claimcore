# One authority, one ledger

Decision made 2026-10-13. Implementation is proceeding in the ordered work packages of the
owner-supplied design pack. This record describes the decision; it does not certify completion.

## Decision and guarantees

ClaimCore is a claims case register over one PostgreSQL database. The thirteen business fields,
nine business commands, pure Domain decisions, individual OIDC principals, default-deny grants,
installation calendar, generated contract and browser presentation remain.

Every accepted case operation and authority change becomes one hash-linked ledger entry in the
same transaction as its projections. Writes serialize on the ledger head before loading any
decision input. Application supplies one pure decision; the storage adapter locks, loads,
persists and commits. Preview is stateless advice. Exact operation identity and content replay
returns the original receipt. Rejection writes nothing. Lost commit confirmation is unknown,
and callers retain and replay the exact request rather than infer failure.

The guarantees are: one decision authority; one record authority; atomic acceptance;
content-bound idempotency; honest uncertainty; revision-aware change; tamper evidence relative
to an externally retained position; locked or snapshot-consistent actor authority; one explicit
calendar; erasable live business payload without breaking the chain; one generated contract
whose fingerprint clients and service must agree on.

The ledger hash covers fixed metadata and a salted payload commitment. Erasure clears payload,
nonce and request digest throughout a case's accepted history. The case reference remains
reserved. VOID, REINSTATE and ERASE are stewardship acts, separate from the business commands.
One human steward may erase at the expected revision.

The host and its administrators, database superuser and schema owner, OIDC issuer and backup
holders remain trusted. Backup, restore, replication, handoff, copy deletion certification,
unaccepted request custody, multi-party approval and data-readiness modes are non-goals.
Erasure does not reach dead row versions, WAL or earlier copies. Restoring an earlier backup
can restore erased payload; verification against a post-erasure retained position detects the
missing history. No installation or durable-format conversion is supplied by this change.

## Alternatives and challenge

Reorganizing the witness and custody code would retain the two-store commit protocol and its
maintenance burden. An optional custody module would still require hooks through acceptance
and reads. Retained preparation creates server-side recovery state; stateless preview and
idempotent execution provide the needed review and retry behavior. Reading only by replay
would make indexed lists and authorization depend on all historical rules; co-committed
projections keep these reads direct and verifiable. The existing contract generator remains
because it already owns strict schemas and browser validators.

The design challenge established that PostgreSQL evaluates a locking SELECT's target-list
clock before its lock wait: the clock must be read in a separate statement after locking.
Runtime lock, statement and idle-transaction timeouts must bound serialization. Unsalted
request digests cannot remain in the hash preimage after erasure. Logging uses a closed set
of metadata fields and never provider messages or claimant content. Durable format numbers
follow their bytes; contract compatibility follows its fingerprint. Stewardship stays separate
from the nine descriptor-driven business commands. Test discovery and project membership
guards replace hand-kept test registries. These conclusions still require product tests as
their work packages return the components to the solution.

## Owner decisions

The owner authorized the scope cut, retaining the case reference after erasure, and allowing
one steward to erase. They are implemented as specified as the relevant packages land.
No merge, release, version change or repository-protection change is authorized.

## Changes during implementation

### Material verification guards

WP0 inspection found that the pack's `materials/db/validate.sh` printed lock timestamps
without comparing them, counted ERROR lines without checking SQLSTATEs, and used `grep -qv f`
on the two chain results. That pipeline could accept one true result alongside a false one.
The extracted script now compares the waiter timestamp with the holder's release boundary,
checks all eighteen SQLSTATEs in order, and requires exactly two true chain results.
Negative controls rejected a clock read before the lock wait, false chain results, and a
division-by-zero error substituted for a privilege refusal while preserving the count of 18.
The corrected positive run printed `validation passed`. No schema correction was necessary.
These are material-check corrections, not changes to the product guarantees.

### Workflow audit syntax

The draft release job lacked a name; pedantic zizmor reported it, and the job now has one.
Removing the existing blanket zizmor configuration exposed five `self-repository` findings.
Changing those references to GitHub's `$/` syntax made actionlint 1.7.12 reject all five as
invalid action references. The workflows retain workspace-relative references after checkout,
with one rule-specific inline reason per occurrence. Both tools pass with five explicit
ignored self-repository recommendations. This keeps the compatibility limitation visible;
it does not suppress the other workflow audits.

### Local gate shell style

ShellCheck with the retained strict shell configuration reported four issues in the draft
`verify.sh`: bracket-test style, two unbraced variables and a masked `uname` status. The script
now uses Bash double brackets, braced variables and a separate platform capture. No gate
command or test requirement was removed. `.shellcheckrc` remains because shell scripts remain;
this follows the disposition note rather than its unconditional DELETE classification.

### Stewardship reason admission

The unmodified material threw `NullReferenceException` for a null reason and accepted a lone
UTF-16 surrogate. This was reproduced with an absolute-path F# script before correction.
The existing reason validator already used strict UTF-8 admission before counting runes.
Stewardship retains that mechanism with the material's 200-scalar bound and rejects blank,
surrounding whitespace, control, format and separator characters. Both the product and the
extracted material are corrected. The next revision is computed only after the exhaustion
check. Comments now describe removal from the live database, consistent with the erasure
limits; they do not promise deletion from backups.

### Pending-test refusal

The test-plan material says the existing runsettings reject focused and pending tests.
A native fixture disproved the pending part: one normal plus one pending test returned exit 0,
with one success and one skip. The settings do reject focus: a focused fixture with settings
ran zero tests and exited 8 under the minimum-test policy; the same binary without settings
ran its test and exited 0. The shared guard now checks the discovered tree for both modes.
The pending fixture with that guard exited 2, with one failed guard, one success and one skip.
Focused settings and the shared guard are complementary; neither is presented as a substitute
for full native execution.

### Source membership at the frontier

WP2 requires a Domain-only test project and a guard against uncompiled files in its directory,
while retained unit files for Application, Contracts and adapters were scheduled to return in
later packages. Leaving those files uncompiled would violate the guard immediately. They are
removed from the active directory now and will be restored from Git as their layers return.
This is an ordering correction; their kept behavior and oracles are not abandoned. The three
guards remain linked shared files, with no hand-kept test registry or exclusion list.

Source membership uses MSBuild's evaluated Compile items in the executing assembly's build
configuration. A literal XML Include with a false condition is not proof of compilation.
Negative controls cover an uncompiled signature, a missing source and a condition-excluded
source. Reflection controls cover both a public static property and field without `[<Tests>]`.

## Preflight evidence

The supplied archive SHA-256 and sidecar both matched
`52f546e37d5512a79674cad085314249a1031d8a78b2b564793e8e674ce81055`.
It was extracted into a fresh directory outside the repository. Its Markdown materials and
the scripts used in WP0 were read before execution.

Fetched `main` is `2815f94fa9a7a75e1ed726a60b9fc09c823bdabb`, containing audited revision
`2c597f50dc02ba517f5c3e1f7cf75b2499ceedf5`. PR #90 had already merged `codex/ci-date-proof`;
there were no other local branches or open pull requests. Reclassification of the fetched
revision produced a byte-identical disposition inventory. No other branch was changed.

Tool versions were .NET SDK 10.0.401, Node 26.10.0, npm 11.19.1 and Docker client/server
29.8.2. Locked solution restore passed. Separate Domain and RecordFormat Release builds passed
with zero warnings and errors. The required existing runner,
`node eng/ci/suites/suite.mjs run unit --build`, passed all 414 tests, zero failed and zero
skipped, in 40.205 seconds of test execution. Its full original solution build passed with
zero warnings and errors in 5m55s.

The absolute-path `dotnet fsi --quiet` reference run reproduced all three ledger vectors.
The corrected absolute-path database validation applied the baseline as a non-superuser,
checked time ordering, matched 18 refusals and printed two true chain results. Every disposable
validation container was labelled and removed with its anonymous volume. The ten evaluation
containers present at preflight were never connected to, stopped or removed.

The release draft's read-only CI filter returned `success` for the real `v0.8.0` push run and
no success for a nonexistent tag. Its changelog extraction produced 46 lines for 0.8.0 and
none for an absent version. No release workflow was dispatched.

## WP1 frontier

WP0 and WP1 are complete. WP2 has not started. The solution currently contains only Domain
and RecordFormat, whose source bytes are unchanged from the base. It contains no test project;
the baseline 414-test result above precedes the frontier change and is not a test result for
the eventual ledger implementation.

WP1 removed 1,502 classified paths and the blanket zizmor configuration, replaced the two
workflows, installed the local gate and relaxed only file, module and value length lint limits.
The strict compiler, function and complexity limits remain. The pinned PostgreSQL image record,
contract generator, rejection examples, license inputs and test settings remain. Shared build
properties and the toolchain action needed no changes. Package pruning waits until WP13.

On this frontier, locked restore and `dotnet build ClaimCore.slnx --configuration Release`
passed with zero warnings and errors. `dotnet fantomas --check src tests eng` and
`bash eng/Check-FSharpLint.sh` passed across all remaining sources. ShellCheck passed on the
two retained engineering scripts; `bash -n eng/verify.sh`, actionlint and pedantic zizmor
(both offline and online) passed. Zizmor reports five rule-specific ignored recommendations,
explained above. The Gate shell body passed with all-success results and rejected a failure
and an empty result set. The language/path sweep leaves only the HostSecurity C shim, to be
removed in WP6, and shell scripts only under eng and deployment. Version, published changelog,
Domain and RecordFormat bytes remain unchanged.

The full local gate, final CI, ledger integration, host, frontend, published acceptance and
manual browser walkthrough are not verified yet. Their inputs return in later packages.
`SET-IN-WP15` test floors remain intentionally unresolved until measured at the final frontier.
The next step at this boundary was WP2: replace lifecycle/erasure/hold Domain types with Stewardship, preserve
the nine business commands, restore the Domain-only test project and prove the three test
discovery guards with negative controls.

## WP2 frontier

WP0 through WP2 are complete. The solution contains Domain, RecordFormat and the Domain-only
ClaimCore.Tests project. WP3 has not started. Domain's business model file is named CaseModel;
stewardship replaces lifecycle, holds and erasure evidence types. The nine business commands,
their validation, corrections, eligibility and descriptors are unchanged. Domain's remaining
internals grants already name only Application and Tests and required no edit.

Twelve retained test/oracle files were compared with the WP1 revision and are byte-identical
after removing only their added discovery attributes. The shared fixture loses only the
Application-specific helpers. The property harness gains the stewardship property identity.
Inspection of the fuzz suite found no direct Domain decoder property to transfer now; its
RecordFormat and transport properties return with those layers. The existing scalar and
transition properties remain active.

The restored project lock was generated with `dotnet restore ClaimCore.slnx --force-evaluate
-p:RestoreLockedMode=false`; removed project and transitive dependencies disappeared. Locked
restore then passed. `dotnet build ClaimCore.slnx --configuration Release --no-restore` passed
with zero warnings and errors. The complete native test command uses the project, Release,
`--no-build --no-restore --minimum-expected-tests=110 --zero-tests-policy=strict --timeout=5m`
and `-- --settings=/Users/erst/Tools/claimcore/eng/expecto.runsettings`. It passed 112 tests:
112 succeeded, zero failed and zero skipped. Eight guard tests and seven stewardship tests
join the 97 retained tests. The separate adversarial fixture runs above are negative controls,
not qualifying suite results. Initial compiler failures in added test support were fixed;
they are not counted as passing runs.

Fantomas passed across src/tests/eng. The full FSharpLint wrapper passed, and the two changed
guard files were checked again after the final source-membership correction. The stewardship
material checks and native guard fixtures ran outside the repository. No container or database
was started in WP2. Product version, changelog, AGENTS engineering principles and RecordFormat
bytes remain unchanged.

Next is WP3: remove recovery artifact codecs, add canonical stewardship/authority records and
ledger encoding, reproduce independent ledger vectors, and restore the unchanged canonical
request/snapshot vector and property tests. WP3 through WP15, the full gate and final product
acceptance are still unverified.

## Canonical ledger payloads

Stewardship and authority payloads are parts of ledger entry format 1, not independently
numbered protocols. The canonical command format remains 3 and snapshot format remains 2.
JSON uses the existing RecordFormat writer, fixed property order and exact authored strings.
Decoders reject missing, duplicate or extra properties, unknown action/scope tags, malformed
Unicode, noncanonical or empty UUIDs and wrong JSON scalar types. Domain and Application still
own business and authority admission; structural decoding is not authorization.

Stewardship fields are `operationId`, `caseReference`, `expectedVersion`, `action`, in that
order. Action is `{type: VOID, reason}`, `{type: REINSTATE, reason}` or `{type: ERASE}`. Erase
accepts no reason field. Authority fields are `eventId`, `target`, `action`. Target is
`{kind, issuer, subject}`. Action is REGISTER_ACTOR with no additional fields, SET_ENABLED
with `enabled`, or SET_GRANT with `role`, `scope`, `active`. Scope is `{type: INSTALLATION}`
or `{type: CASE, caseReference}`. Kind, role and principal strings are format data; Application
admits their meanings from its own types and policy. This avoids a reverse RecordFormat-to-
Application dependency or a second authorization matrix.

Ledger framing is the two big-endian unsigned lengths followed by their exact bytes. Commitment
is SHA-256 of the 32-byte nonce followed by the framed payload. The 185-byte preimage binds
installation, sequence, previous hash, kind, operation, actor, signed UTC microseconds, case
metadata and commitment. It has no payload, nonce or request-digest input. A sub-microsecond
instant is refused rather than rounded. Act codes follow the pack's fixed 1–12 table. Authority
scope writes zero case placeholders; kind distinguishes it even when a real case date is day 0.

## WP3 frontier

WP0 through WP3 are complete. The solution still contains Domain, RecordFormat and Tests.
WP4 has not started. Recovery artifact/envelope codecs are removed; canonical stewardship and
authority codecs and ledger encoding are present. The three ledger vectors were copied from
the verified pack without regeneration. Canonical request/snapshot vectors are byte-identical
to the base and pass. Request UUID admission was moved to the shared JSON helper with the same
lexical checks and diagnostic text; request/snapshot writer bytes did not change.

The existing canonical record tests and properties return with their oracles preserved.
Their Application dependencies are replaced by Domain validation and RecordFormat digesting;
they do not stand in for the Application tests due in WP4. Byte corpora and the RecordFormat
decoder-totality property return from the fuzz suite, expanded to stewardship, authority and
framing. The remaining transport/cursor fuzz properties still await their layers.

Generated the test lock with `dotnet restore ClaimCore.slnx --force-evaluate
-p:RestoreLockedMode=false`; its only added project dependency is RecordFormat. Locked restore
passed. Release solution build passed with zero warnings/errors. The complete native MTP run
uses `--minimum-expected-tests=130 --zero-tests-policy=strict --timeout=5m` and the existing
Expecto settings: 136 total, 136 succeeded, zero failed/skipped. Initial test-support overload
errors were corrected before this run and are not counted as passing builds.

Tests check every vector's payload, commitment, full preimage and hash; all metadata-field
mutations; the full unsigned sequence width; signed pre-epoch microseconds and equivalent
offsets; day-zero kind separation; arbitrary framing round trips; total decoding on noise and
mutated valid seeds; and recomputation after clearing erasable buffers. Each added guard has
rejecting cases, including malformed shapes, lengths, UUIDs, case revisions, rule revision,
nonce width and timestamp precision. These are encoder/decoder results, not evidence that
database erasure or transaction behavior has been implemented.

The native FSharpLint function limit was given a 67-line function in an external fixture:
it reported FL0025 and exited 255 against the retained 50-line rule. No lint limit or exception
was changed. Product version, changelog, AGENTS principles and Domain bytes are unchanged.
WP3 starts no container or database. No design non-goal was reopened.

Next is WP4: the pure Application decisions, actor-bound facade, five-role policy and in-memory
ledger store with its shared store contract. WP4 through WP15 and final acceptance remain
unverified; the full gate cannot pass at this frontier.

## Changes during implementation: Application seam

The material could not provide stateless preview's operation lookup or definition's captured
instant in the same snapshot, and its list page omitted the actor needed for OPEN advice.
ReadCase now takes an optional operation identity and returns the existing entry; ReadActor
returns its instant; a case page includes its actor. Authority entries carry a derived target
actor ID in the store view so replay and observation need no second authority snapshot. That
ID is derived from recorded principal identity and the actor projection, not added to the
hash preimage or SQL schema. The immutable installation ID is supplied to the internal core
factory by composition. These are input corrections for G7–G9, not storage-owned policy.

Pure callbacks need to distinguish a stored-integrity fault from a business refusal. Decisions
and write results therefore include a failure case that writes nothing. Inconsistent stored
request bytes/digest reach OperationContentConflict; the fourth fault has a real path.

Initial-owner provisioning must record its owner grant for a verifier to reconstruct it. The
canonical authority subtype PROVISION_OWNER records that combined initial authority fact.
It is human-only, refuses an existing actor population, and can exactly replay its own entry.
Only owner administration uses its internal decision function; no actor-bound management
method accepts it and no endpoint is added. AuthorityLockedView includes the actor count for
that precondition. Case IDs and newly registered actor IDs use their globally unique accepted
operation/event IDs, avoiding another random input to pure decisions.

Stewardship lacked shape admission before encoding/I/O. Domain now shares its existing
envelope checks between CommandRequest and StewardshipRequest, with stewardship's own reason
check. The nine business-command checks retain their order and outcomes. Malformed native
stewardship input is refused before storage. The independent Domain tests remain unchanged.

Principal constructors previously admitted malformed UTF-16, which JSON encoding can replace,
and NUL, which PostgreSQL text cannot represent. They now require well-formed UTF-8 and reject
NUL while preserving all admitted identity text exactly. No normalization or case folding
was introduced.

## Implementation frontier

WP0 through WP4 are complete. Application returns to the solution alongside Domain,
RecordFormat and Tests. The actor-bound facade has preview/execute/stewardship, reads and
authority management. Decisions use one role/capability table and supplied locked/snapshot
views; they obtain no clock, randomness, file, environment or network API. Source inspection
found none of those APIs. Compiled architecture enforcement remains due in WP13.

The in-memory store publishes one immutable state containing entries and projections under
one gate, calls each write decision once, clones byte buffers, and returns authority/data from
one read view. Its seven shared contract tests are parameterized by a fresh fixture factory
and linked for reuse by PostgreSQL in WP5. Fault injection is explicit test behavior and does
not establish real commit-loss classification; that remains a PostgreSQL acceptance item.

Locked restore and Release solution build passed, with zero warnings/errors. The complete
native MTP command used minimum 160, strict zero-test policy, timeout 5m and Expecto settings:
168 total, 168 succeeded, zero failed/skipped. Property-profile/recheck overrides were absent.
The 12 retained Domain test/oracle files were compared byte-for-byte and are unchanged.
The 1,080-case independent role × capability × scope × principal matrix passes. Tests cover
all six execution outcomes; landed/unlanded unknown replay; exact identity after later
revisions; inaccessible/absent/erased equality; grant loss; case-scoped creation denial;
stewardship; last-owner protection; operation scope separation; cursor refusals; original
authority targets; atomic failed projections; and post-commit interruption.

An initial preview test expected AlreadyExists where established Domain ordering returns
VersionConflict; its expectation was corrected against Domain, not the implementation.
Negative controls exposed observer scope errors and test-store interruption/rollback errors;
those were fixed, then the full suite passed. Initial compiler and lint failures were repaired
without suppressions or relaxed limits. Version, changelog and AGENTS principles are unchanged.
`dotnet fantomas --check src tests eng` and `bash eng/Check-FSharpLint.sh` passed across the
remaining tree after the final shared-contract refactoring. The source purity/stale-capability
searches and `git diff --check` passed. Generated lock diffs were inspected separately from
execution: the test graph adds Application, and Application's RecordFormat edge now records
the current product version. No package version changed.
WP4 starts no container or database and reopens no non-goal.

Next is WP5: the PostgreSQL ledger store, schema/migrations/admission/verification, owner
provisioning and integration project. Run the same shared contract plus real concurrency,
timeout, commit-loss, privilege and tampering controls on newly labelled disposable resources.
WP5 through WP15 and the final gate/acceptance remain unverified.

## Changes during implementation: retained reference and commit confirmation

A real PostgreSQL counterexample exposed a gap in G2 and G7. After an accepted ERASE, updating
only `cases.case_reference` still passed verification against the erasure receipt. The complete
57-case run had 56 successes and one failed rejecting test. Payload deletion removed the only
ledger-to-reference binding; a tombstone shape check cannot prove its retained reference.

The correction uses the existing hash-covered case UUID as the reference binding. It is the
first 16 bytes of SHA-256(ASCII `claimcore/case-reference-id/1\n` || installation UUID in network
byte order || strict UTF-8 reference), interpreted as a network-order UUID. Application uses
this identifier for OPEN; verification derives it from each projection reference and checks
its case history. No normalization, trimming or case folding occurs in this encoding. Actor
IDs remain registration event IDs; case identity is independent of operation identity.

The separate challenge considered retaining a reference field/hash in each entry, changing the
preimage, or retaining erase-request bytes/nonce. Those add schema/format state or alter the
specified erasure boundary. Reference-bound case identity changes neither the seven-table
schema, 185-byte entry layout, existing vectors nor retained/removed data. Reference binding
has 128-bit second-preimage strength; ledger hashes remain SHA-256. Predictable identifiers
confer no grant. An independent Python implementation printed three vectors, reproduced by
native tests, with namespace, case, whitespace, normalization and malformed-scalar controls.
The extracted format material documents the correction. No installed data is converted.

The pack's assertion that any SQLSTATE proves a failed commit was also false. A protocol relay
withheld PostgreSQL's real COMMIT completion, then terminated only that fixture's backend and
forwarded its real fatal `57P01` response. The commit had landed. The complete 61-case run had
59 successes and two failures: case execution reported failure, and initialization propagated
57P01 as failure. Both violated G5. Server errors establishing abort remain definite; fatal or
panic replies, connection-class errors and declared completion uncertainty after commit
dispatch remain unknown. Before dispatch, these failures are definite. Cancellation is checked
immediately before attempting commit; after entering the commit API, lost confirmation is
conservatively unknown. Store and administration share that classification. Transport and
57P01 paths use real connections; PANIC/08007/40003 taxonomy controls use constructed exceptions
and do not establish physical failure injection for those states.

Runtime ownership admission measures both immediately inherited and assumable schema-owner
power with `pg_has_role(..., 'USAGE')` and `pg_has_role(..., 'SET')`. Native fixtures show direct
owner equality false while these powers exist, and admission refuses both configurations.
The meanings are specified by PostgreSQL's [privilege inquiry functions](https://www.postgresql.org/docs/18/functions-info.html).
This closes an implementation defect rather than adding an actor role or authorization matrix.

The system-zone list on this macOS/.NET runtime includes `UTC` as an IANA ID despite its alias
status. The external probe printed `UTC eligible=true resolved=UTC IANA=true`. Initialization
requires `Etc/UTC` for that calendar and continues to refuse aliases/platform names. The
in-memory store also used UTF-16 ordinal order, which disagrees with PostgreSQL C/UTF-8 order
for BMP versus supplementary references. The shared Unicode continuation test failed with
168 successes/one failure before correction. Its comparator now uses UTF-8 bytes; the scoped
visibility fixture obtains the actual case ID rather than assuming it equals an operation ID.
These corrections preserve G1–G11 and reopen no section 4.3 non-goal.

## WP5 frontier

WP0 through WP5 are complete. The solution contains Domain, RecordFormat, Application,
Postgres, Tests and IntegrationTests. Postgres owns raw Npgsql commands adjacent to their
capabilities, one data source per runtime/administration instance, RC writes with head-first
business loads and one pure callback, atomic entry/projection persistence, bounded sessions,
and repeatable-read snapshots. Runtime admission checks transport, supported major from the
embedded authoritative image record, effective role power, exact migration prefix/digests,
installation/calendar and head/last-entry agreement. Schema administration initializes fresh
or applies forward scripts; foreign histories are refused without changes. Verification streams
ledger rows once, checks metadata/link/payload commitments, contiguous case revisions,
reference-bound live/tombstone projections, authority folds and optional retained positions.

Commands actually executed against the final code:

- `dotnet restore ClaimCore.slnx --locked-mode`: exit 0; all projects up to date.
- `dotnet build ClaimCore.slnx --configuration Release --no-restore`: exit 0;
  `Build succeeded`, zero warnings/errors, 3.33s on the last build.
- `dotnet test --project tests/ClaimCore.Tests/ClaimCore.Tests.fsproj --configuration Release
  --no-build --no-restore --minimum-expected-tests=172 --zero-tests-policy=strict --timeout=5m
  -- --settings=/Users/erst/Tools/claimcore/eng/expecto.runsettings`: 172 total/succeeded,
  zero failed/skipped, exit 0, 5.113s.
- The same native command for `tests/ClaimCore.IntegrationTests/ClaimCore.IntegrationTests.fsproj`
  with minimum 62: 62 total/succeeded, zero failed/skipped, exit 0, 89.602s. No focused, filtered,
  pending, retried or sharded invocation qualifies these results.
- `dotnet fantomas --check src tests eng`: exit 0, no output.
- `bash eng/Check-FSharpLint.sh`: exit 0, zero warnings across the remaining tree. Function and
  complexity limits remain unchanged; no new exception or suppression was introduced.
- `python3 -I /Users/erst/Tools/claimcore-ledger-plan-review-20261013/wp5-case-reference-id-reference.py`:
  printed the three reference vectors recorded above; native tests independently match them.
- `cmp db/migrations/0001_baseline.sql <absolute extracted materials/db/0001_baseline.sql>`:
  exit 0, no output; baseline bytes are unchanged. Its embedded digest is checked against raw
  repository bytes in the integration project.
- `git diff --check`: exit 0. Docker listings show no test-labelled containers or volumes after
  the final run; the same ten evaluation containers remain running/healthy and were never
  connected to. Each fixture uses a fresh labelled pinned-image container with tmpfs storage,
  cleanup on setup/test failure, and a unique run label. Failed runs were also checked for cleanup.

The shared store suite now has eight cases, including Unicode ordering/continuation, and runs
on both stores. Real PostgreSQL evidence covers 20 concurrent writers, lock/instant ordering,
stale-head refusal, all three timeouts, deferred commit abort, landed/unlanded lost reply,
post-commit fatal 57P01, exact replay, all eighteen material SQLSTATE refusals, admission
refusals including inherited/assumable ownership, migration digest/idempotence/rollback,
owner bootstrap/replay/refusals, stable reads across a concurrent change, seven basic tamper
classes, restored valid erased payload, altered erased reference and an actual pg_dump/restore
before erasure checked against its later receipt. Disabled timeout/trigger, pre-lock clock and
mutated ledger/projection controls establish detection, rather than merely exercise code.

Source inspection is separate: the public storage surface is the admitted opaque Runtime and
five-operation Administration class, with no public Npgsql/store/decision callback type. Npgsql
compile/build assets do not flow to consumers. Canonical request/snapshot writer code and the
three supplied ledger vectors were not changed. Domain, product version, changelog and AGENTS
engineering principles are unchanged. Generated lock files were inspected: new graphs contain
the pinned Npgsql/Testcontainers dependencies and existing direct project edges; no package
version changed. These inspections are not the compiled architecture gate due in WP13.

Initial compiler, lint and fixture failures were fixed, not counted as passes. The first
parallel fixture run passed 14/15 and timed out creating one container; sequential fixtures
passed 15/15 in 15.649s. Relay setup failures exposed GSS negotiation and redacted-password
handling; they were repaired before qualifying real-boundary runs. The reference, Unicode,
UTC and fatal-commit counterexamples and corrections are documented above with their outcomes.
No existing installation or private evaluation data was opened, reset, migrated or deleted.

Next is WP6: portable Unix HostSecurity and Hosting composition over the admitted runtime and
stored calendar. WP6–WP15, published-binary/Keycloak/browser acceptance, Linux application
builds and the final full gate remain unfinished/unverified. The PR remains draft and unmerged.
