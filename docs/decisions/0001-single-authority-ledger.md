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

## Changes during implementation: file admission and runtime composition

The file material's regular-file assumption failed a macOS/.NET probe: a FIFO reports
`Exists=true, Attributes=Normal, Length=0`; opening it read-only waits for a writer before
`CanSeek` can reject it. `/dev/null` reported `Attributes=Normal, CanSeek=true, Length=0`.
.NET's Unix open implementation does not set O_NONBLOCK for public FileStream options, and
public attributes do not expose Unix file kind. The implementation uses a bounded stock Unix
`stat` type probe before opening, followed by the specified handle-based mode check. It uses
ArgumentList, a C locale, no shell, no path/content in failure values, a two-second probe bound,
and fails closed if the utility is absent/fails. The private reader still has no write operation,
C shim, compiler prerequisite or source interop declaration. macOS/BSD and Linux/GNU stat are
now explicit prerequisites. The host-trust concession remains: path/type/link checks do not
provide a no-follow ancestor walk or defend against a trusted local replacement race. The
buffer size is captured once to preserve the byte bound if file size changes.

Owner-read permission is required even for root; otherwise admission reports Unreadable. This
makes the unreadable-file guard deterministic on both supported platforms. Directories report
NotRegular; dangling and live leaf links report IsLink. Unsupported platforms refuse before IO.
The extracted material is corrected; no retired private-file writer or key mechanism returns.

F# compiler evidence FS0074 showed that implementing IActorClaimsCore needs Domain's command
and stewardship types as a direct Hosting dependency. The material graph adds that edge. The
concrete Postgres runtime's internal Npgsql constructor also leaked a compiler dependency into
Hosting. It is now a private implementation behind IRuntime; Hosting similarly returns an
opaque interface and its own typed open-fault union so Web needs neither Postgres nor Npgsql.
No new callback/store surface is public. Postgres admission returns typed refusals and disposes
failed opens; the supported major now comes from the authoritative embedded server record.

The material's TimeZoneInfo allow-list conflicted with section 5.6's storage admission and
initializer zone checks. Its list now permits Postgres for those checks, alongside Hosting and
Database. Hosting resolves one immutable calendar object per admitted runtime, never per call,
and derives dates from supplied instants. This preserves G9 and keeps timezone decisions out
of Domain, RecordFormat, Application, Contracts and case-work adapters.

An initial nested async exception handler compiled but produced InvalidProgramException in
runtime admission: 73 total, 60 failed, 13 succeeded, no skips. Admission was split into bounded
role, installation and head loads; synchronous calendar validation avoids that generated task
shape. Fixture rethrow now preserves the original stack with ExceptionDispatchInfo. The full
suite was rerun; the failed run is preserved outside source and is not counted as passing.

## WP6 frontier

WP0–WP6 are complete. HostSecurity retains PrivateFile and TlsCertificatePurpose only, with
its existing test friend. The shim, native targets/imports, private writers and three key-file
modules are removed. Hosting composes admitted storage and the stored calendar into actor-bound
facades. Every facade method, including management/observation, holds an admission lease.
Shutdown closes admission, drains within a finite bound, closes the source exactly once at
drain or expiry, and leaves already-admitted results unchanged. A real Npgsql transaction waiting
on the head still commits after source disposal at bound expiry; no result is relabelled.

Executed commands and outcomes on the final code:

- `dotnet restore ClaimCore.slnx --locked-mode`: exit 0, projects up to date.
- Release solution build with `--no-restore`: exit 0, zero warnings/errors. Eight projects build.
- Complete native MTP unit run, Release/no-build/no-restore, minimum 191, strict zero policy,
  5m timeout and `eng/expecto.runsettings`: 191 succeeded, zero failed/skipped, 7.020s.
- Complete native MTP integration run with minimum 75 and the same settings: 75 succeeded,
  zero failed/skipped, 126.333s. Unsupported-server metadata uses the actual admission guard
  and typed Hosting mapping; a physical PostgreSQL 17 server was not started.
- `dotnet fantomas --check src tests eng`: exit 0, no output. Full FSharpLint: exit 0, zero
  warnings; no suppression, exception or limit was added.
- Fresh labelled SDK container at
  `mcr.microsoft.com/dotnet/sdk:10.0.401@sha256:e70cdb7f80b0348f5cb85f19a8f670fca061f033d57eed12fa003d58b0e06317`,
  using a clean tracked/unignored source copy mounted read-only and temporary build/results
  directories: printed `No C compiler present`, locked restore passed, Release build succeeded
  with zero warnings/errors in 24.69s, and the complete Linux native unit run passed 191/191,
  zero failed/skipped in 4.791s. The script was read then invoked by path outside the extraction.
- The first Linux test launch wrote to read-only `/source/TestResults`, failed before discovery
  with zero tests/exit 134, and does not qualify. Explicit temporary results fixed the launch;
  the complete suite then passed without filtering or weakening its guards.
- Tracked-file/source scans print no C file, DllImport, LibraryImport or native-target import.
  Cached diff whitespace check passes. Only this task's newly created labelled containers were
  used; cleanup leaves none and the ten evaluation stacks remain running/healthy and untouched.

The private-file table covers owner-only read modes, every group/other permission bit, exact
bounds/empty files, absolute/missing paths, live/dangling links, unreadable mode, directory,
FIFO without a writer, and unsupported-platform pre-IO refusal. Native Windows execution was
not run; the platform guard was tested on supported hosts. Runtime tests cover successful open,
all schema/role/installation/head/transport refusals as typed values, cancellation, stored-zone
midnight, admission close, finite bounds, idempotent disposal, accepted-result preservation,
and real in-flight work past drain expiry. These are not full published service/CLI acceptance.

Source inspection separately confirms no obsolete runtime supervisor, audit, health, custody,
quarantine or key path; Hosting has no Npgsql compiler reference. TLS purpose code, Domain and
RecordFormat bytes, product version and changelog are unchanged. Lock diffs add Hosting and
HostSecurity project edges only; no package version changes. The compiled architecture gate
is still WP13. Corrections reopen no design non-goal. Next is WP7 (Contracts); WP7–WP15 and final
published/Keycloak/browser acceptance remain unfinished. The PR stays draft and unmerged.

### WP7 — One service contract

Contracts and its generator return to the build frontier. One catalogue defines fourteen
literal routes, their methods, their complete request/response schemas and the five durable
mutation endpoints. HTTP responses and CLI frames share outcomes; one SHA-256 fingerprint
covers the routes, schema documents, CLI response frames and typed host failures. Missing or
unequal handshake values fail the exposed admission predicate; host dispatch integration is
WP8. The CLI envelope preserves the exact frame and input bytes with defensive copies.
Stateless preview, stewardship, case disposition, erasure views, ledger positions and authority
outcomes replace the removed wire families. Administration has no Contracts artifact.

Implementation corrections (no design guarantee or non-goal changes):

- The saved intake prototype's ERASE branch failed FS0001. Its check is now a separate statement
  before constructing the action. Strict schema admission precedes all typed endpoint intake.
- An initial role schema used READER/EDITOR/STEWARD instead of Application's CASE_READER,
  CASE_EDITOR/DATA_STEWARD. It now derives all five tokens from `Roles`; an independent token
  table admits the five documented names and rejects the alternate spellings.
- Ledger sequence and case revision have different upper bounds. Reusing the revision schema
  wrongly refused the positive BIGINT maximum allowed by the SQL baseline. An absolute-path
  F# probe printed `positive BIGINT maximum admitted: false`; after using a positive Int64
  schema it prints `true`. A native test accepts the maximum and rejects zero, negative,
  overflow and noncanonical spelling. Sequence and revision are decimal strings on the wire,
  preserving exact values in JavaScript.
- The generation driver referenced the deleted SDK-host tool. It now invokes the ordinary
  `dotnet` executable and checks its exact version against `global.json` before running the
  generator. No discovery wrapper or alternative toolchain selector was added.
- The generator consumes only Contracts; default English templates and public product metadata
  are projected by their existing contract/presentation owner. Direct Application/Domain and
  deleted Database/Postgres generator dependencies are unnecessary. The extracted graph is
  corrected to this compiled project edge; repository graph enforcement remains WP13.

Executed on the completed package:

- `dotnet restore ClaimCore.slnx --locked-mode`: exit 0. Reviewed lock changes restore the two
  projects/test edge and remove obsolete runtime dependencies; no product/package version bump.
- `dotnet build ClaimCore.slnx --configuration Release --no-restore`: exit 0, ten projects,
  `Build succeeded`, zero warnings/errors, 5.21s.
- Complete native MTP unit command, minimum 207, strict zero-test policy, five-minute timeout,
  prescribed Expecto settings: **207 total/succeeded, 0 failed/skipped, 17.348s**.
- Complete native MTP PostgreSQL command, minimum 75, same settings:
  **75 total/succeeded, 0 failed/skipped, 113.849s**. No filtering or retries qualify either suite.
- `dotnet fantomas --check src tests eng` and `bash eng/Check-FSharpLint.sh`: exit 0, no formatting
  findings and zero lint warnings. Function/complexity limits and exceptions are unchanged.
- `npm --prefix web run contract:lock`: exit 0; intentional lock diff reviewed. Then two
  `npm --prefix web run contract:generate` runs, each after removing only the 51 regular files
  in the generated output directory: both exit 0 and reproduce the accepted lock.
- `npm --prefix web run contract:verify`: exit 0. Manifest/lock/output equality probe printed
  `51 exact manifest/lock artifacts; 14 endpoints; no removed-family artifacts`.
- `node --test web/scripts/validator-groups.test.mjs web/scripts/endpoint-corpus.test.mjs
  web/scripts/contract-lock.test.mjs web/scripts/contract-promotion.test.mjs`:
  **17 passed, 0 failed/skipped/cancelled/todo**, 234.778ms. All 199 shared corpus cases agree:
  100 positive and 99 negative, covering every endpoint/direction and Unicode reference bounds.
  Promotion/hash/inventory/validator guards retain destructive rejecting controls.
- Targeted type-aware oxlint and Prettier checks for every changed generation script/test:
  exit 0. Full frontend typecheck, lint, application tests and browsers remain WP11–WP15.
- Absolute-path sequence probe prints `positive BIGINT maximum admitted: true`. Recursive
  `rg -ni 'v3|v4|protocolVersion'` under Contracts and the generator returns 1/no matches.
  This is a source scan; it is not a compiled architecture check.

Native tests additionally cover every command/stewardship request alternative (32), every
response alternative (62), all six mutation outcomes and exits, authority observation's refusal
of an uncertain result, snapshots absent on erasure, live/voided/erased views, preview, four
faults, new rejections, defensive byte copies, malformed/duplicate/unknown JSON, invalid UTF-8,
lone surrogates and byte bounds. The retained timestamp and independent Unicode scalar oracles
return with their assertions. Deterministic required Hedgehog tests exercise all HTTP/frame
boundaries with arbitrary, hostile and mutated bytes. English templates remain complete.

Early compiler/nullability errors, four lint findings, an incorrect request-count assertion
(199/200 succeeded), a corpus-count assertion and three script lint findings were repaired and
are not passing evidence. The first Prettier invocation used the wrong working directory and
found no inputs; the corrected invocation and check ran against all changed scripts. No guard
was weakened, suppression added or check converted into an expected failure.

Source inspection separately confirms unchanged Domain, RecordFormat, product version,
changelog and engineering principles; generated artifacts remain ignored. The published host,
CLI, owner process, frontend and final Gate remain unfinished. WP8 is next; keep the PR draft.

### WP8 — HTTPS host

Web and its native test project return to the solution. The host maps the fourteen catalogue
endpoints through one schema admission adapter, binds Domain drafts once, and calls only the
actor-bound facade. A missing/different fingerprint is a typed CONTRACT_MISMATCH before core
creation or dispatch. After dispatch, mutations use CancellationToken.None; transport loss does
not reclassify the core result. Cookie/bearer isolation, HTTPS/OIDC/PKCE, server-side tickets,
antiforgery, rate limits and security headers remain. Configuration has exactly nineteen service
names; state-directory exclusion and the removed capability routes/settings are gone.

Readiness is an opaque, read-only runtime operation. Postgres reads checksum-bound migration
history in its bounded snapshot session; Hosting holds an admission lease and returns false
when closed. It exposes no connection/provider/schema-owner surface to Web. Live reports the
process, ready reports database/schema availability. Help describes that condition.

The stderr request record has exactly endpoint, outcome, status, durationMs, actorId and
operationId. Unknown IDs are null. A fresh acceptance proves the current actor ID; the author
of a replayed receipt does not, so replay never attributes that author to the caller. An
unexpected error has a separate closed type/method-stack record. Messages, paths, arguments,
bodies, references and claimant values never enter these records. Framework logging remains
cleared; the bounded diagnostic sink and its counters/correlation identifiers are removed.

Implementation corrections and evidence:

- Runtime had no readiness surface; querying an actor facade would wrongly tie an operational
  probe to actor grants. Added the minimal leased bool operation above. A real PostgreSQL test
  removes payload SELECT permission before admission, observes ready, changes the migration
  checksum and observes false, then closes admission and observes false. No ledger verification
  or case payload scan is performed at startup/readiness.
- WebTests needs Hosting as a direct compiler edge because the typed startup refusal references
  its RuntimeOpenFault. F# FS0074 demonstrated the required type closure. No Postgres edge or
  new internals grant was added to Web/WebTests.
- ASP.NET's nonfile fallback produced 404 for a known dotted path with a wrong method. The
  refusing test failed. Exact method admission now runs before endpoint dispatch, and literal
  path aliases are refused; the test passes without relaxing its expected 405.
- The live identity fixture previously read arbitrary environment-supplied endpoints/private
  paths. It now starts a fresh named/labelled pinned Keycloak with synthetic credentials,
  owned HTTPS material and tmpfs H2 state, cleans only that container, and requires no external
  fixture settings. Its initial audience mapper named a client it did not register, causing
  SecurityTokenInvalidAudienceException. Corrected the fixture to a custom audience as described
  in [Keycloak's mapper reference](https://www.keycloak.org/admin-api/protocol-mappers).
  Production audience validation was not changed.
- Fixture certificate creation could cross the root's second boundary, and macOS refused
  re-export of an imported non-exportable key. Bound the leaf expiry to its root and create the
  configuration PFX directly from its fresh private key. Both failing fixture cases now pass.
- WP8 requires green integration CI, whereas WP1's material defers every minimum to WP15.
  Literal SET-IN-WP15 is not a runnable minimum. The two integration-job floors are set to
  the measured current complete suites (76 and 66); WP15 will remeasure final floors. Other
  jobs' placeholders remain for their packages. This makes the required job executable without
  weakening discovery, focus/pending, source membership, skip or complete-suite guards.

Executed local evidence:

- Locked restore and Release solution build pass; twelve projects, zero warnings/errors.
- Complete native unit suite: 207 total/succeeded, zero failed/skipped, 8.292s.
- Complete native PostgreSQL suite: 76 total/succeeded, zero failed/skipped, 106.145s.
- Complete native Web suite: 66 total/succeeded, zero failed/skipped, 54.912s. Its owned real
  Keycloak test exercises PKCE login, ID-token refusal, bearer case dispatch, mixed credentials,
  tampering, invalid algorithm/type, wrong audience/issuer, expiry and signing-key rotation.
- The HTTP tests cover every endpoint, all six execute outcomes, missing/mismatched handshake
  without factory/core calls, exact shapes/methods/path aliases, antiforgery, logout, operational
  probes and cancellation classification. Canary controls put a unique value in reference,
  request body and exception message; no value appears in request/error records. Replay actor
  attribution has an independent refusing control. Nineteen setting names, defaults, invalid
  bounds, strict UTF-8 and owner-private connection input are checked.
- Full Fantomas and FSharpLint pass with zero findings/warnings. Actionlint and whitespace
  checks pass. Source scans find no removed route/settings, private-file-service, v3/v4 or
  state-directory path in Web. These are inspections/scans, not the compiled WP13 gate.

Early compiler/nullability/type-closure failures, a stale-binary invocation after a failed
build, 53/55 then 54/55 test runs and 55/58 fixture failures are not qualifying evidence.
Failures were diagnosed using stages, statuses and exception types without credential/token
values. No guard was weakened, suppression added or required test made conditional/pending.
The successful command runs are complete, with no filter or test retry. The identity-key refresh
scenario polls the configured refresh transition inside one test, not a rerun of that test.

Product version, changelog, Domain/RecordFormat and existing evaluation stacks remain unchanged.
Published host/client/owner and browser acceptance still await their packages. The integration
job's authoritative result will be recorded for the exact pushed revision. Keep the PR draft.

The completed host suite also retains rejecting controls for body byte limits, I/O failure,
caller cancellation, the absolute acquisition deadline, security/no-store headers, excess
process admission and before-dispatch/dispatched/completed-response failure knowledge. The
PostgreSQL report rerun passes all 76 tests (zero failed/skipped, 96.478s); its TRX is outside
tracked source. Startup latency is instrumented in the physical test; no unreported exact
millisecond value is claimed. The test demonstrates startup without case-payload authority.

The physical startup test additionally requires admission within ten seconds after the fresh
database is initialized. This is the bounded startup criterion, alongside the refused payload
read permission. No exact unreported latency is claimed.

The final physical PostgreSQL run with the ten-second admission assertion passes all 76 tests,
zero failed/skipped, 96.521s. The final Web run including the additional transport controls is
66/66, zero failed/skipped, 54.912s. The CI integration floors are 76 and 66.

A post-push staging audit found the IntegrationTests lock omitted from the package commit.
Adding the central Testcontainers declaration reclassifies its unchanged 4.15.0 dependency as
CentralTransitive. Included that reviewed lock and amended only this task's branch commit with
an explicit remote lease. The earlier pushed revision is superseded; its CI is not passing
evidence. The local locked restore/test input already included the corrected lock.

## Changes during implementation: CLI session uncertainty

The frame envelope has no per-line exit-code member. Continuing an NDJSON session after a
mutation with an undelivered reply could overwrite exit 4 with a later frame's exit code and
hide which exact frame requires replay. A complete 232-test unit run reproduced this: the
new `uncertainty ends a session before another frame can overwrite exit four` test expected
4 but received 3 after a following query (231 passed, one failed, zero skipped, 4.780s).
The failing output is retained outside source as `wp9-session-negative.log`.

The smallest correction is to end the session after delivering an exit-4 or cancelled reply.
The last processed frame is consequently the one to replay. Definite replies continue; an
oversized line ends the session with exit 2; an empty session exits 0. A completed reply's
successful flush clears only that frame's dispatch knowledge. This preserves section 4.1's
uncertain-replay guarantee and reopens no section 4.3 non-goal. It changes no envelope or
fingerprint. The regression remains in the complete suite.

## WP9: authenticated CLI

Moved the authenticated process and protocol implementation into `ClaimCore.Cli`; the
solution now has thirteen projects. The process owns no runtime/database access. Offline
help, product identity, semantic descriptions and schemas remain available without settings
or credentials; discovery reads Contracts' catalogue. Removed the protocol project, recovery
export/import adapters and private-file writing surface. The process reads bounded strict
frames and private inputs, sends the compiled `ClaimCore-Contract` header, uses catalogue
methods and paths, and sends the exact admitted input bytes. Its closed replies and outcome
exits are decoded against Contracts before stdout. Host-failure status, diagnostic and phase
must agree. Every durable endpoint retains uncertainty when dispatch occurred and the reply
is invalid, inaccessible, lost or unflushed.

Retained authorization-code PKCE with S256, the issuer/host/state-bound loopback callback,
client credentials, process-local token reacquisition, monotonic and wall-clock expiry
budgets, system TLS and loopback-only private trust roots. Provider metadata/token readers
are total over malformed input and bounded at 64/32 KiB; bearer text cannot inject a header.
Private bytes are cleared after UTF-8/PEM conversion. Frame input is bounded at 128 KiB and
service response buffering at 16 MiB. Managed SIGINT/SIGTERM callbacks use the current
frame's dispatch knowledge. Session/token ownership has no durable credential cache.

Commands executed on macOS arm64 (native .NET 10.0.401), separately from inspection:

- `dotnet restore ClaimCore.slnx --locked-mode`: exit 0, thirteen restored projects. Reviewed
  all three changed surviving locks: CLI, unit tests and Web tests; project dependencies
  replace the removed protocol assembly. Existing product version remains 0.8.0.
- `dotnet build ClaimCore.slnx --configuration Release --no-restore`: exit 0, thirteen
  projects, zero warnings/errors, final 5.68s.
- Complete native `dotnet test --project tests/ClaimCore.Tests/ClaimCore.Tests.fsproj
  --configuration Release --no-build --no-restore --minimum-expected-tests=232
  --zero-tests-policy=strict --timeout=5m --
  --settings=/Users/erst/Tools/claimcore/eng/expecto.runsettings`: 232 passed, zero failed or
  skipped, 20.935s. Includes every exit-table row, every endpoint method/header/request,
  every result and host-failure phase, response size/media/status rejection, malformed
  provider input, bearer-header rejection, cancellation, stdout failure, independent token
  clock budgets, callback/state/redirect guards and a real TLS reply-loss boundary. The
  latter reads the entire `authority.register` POST and closes the TLS connection without
  an HTTP reply; the actual client returns exit 4 and preserves request bytes.
- The same complete native Web command, project `tests/ClaimCore.WebTests`, minimum 66:
  66 passed, zero failed/skipped, 81.448s. Within the owned live Keycloak fixture, CLI code
  discovers the issuer through its private TLS root, reads its own freshly created private
  secret, obtains a real service token, sends it through Web issuer validation and decodes
  a success reply. The provider rejects a wrong secret. Existing PKCE browser, invalid
  token/audience/issuer/TLS and key-rotation checks remain in this complete run.
- `dotnet fantomas --check src tests eng`: exit 0, no formatting findings.
  `bash eng/Check-FSharpLint.sh`: exit 0, all roots report zero warnings. No limit or guard
  was weakened and no exception added.
- `npm --prefix web run contract:generate`: exit 0. Generated bytes match the existing
  contract lock; decoding typed host failures changes no published schema or fingerprint.
- A Python subprocess check of the built CLI, with all ClaimCore settings removed only
  from the child environment: twelve help/version and discovery/identity invocations
  exit 0 (JSON outputs parsed); unsupported invocation exits 64 without stdout; an invalid
  `call` exits 2 with one protocol-failure frame; a valid unconfigured `call` exits 3 with
  one local-failure frame. No credential or service connection was attempted.

Inspection: CLI has no protocol-generation member, removed project dependency, recovery
adapter, private writer, runtime/Hosting/Postgres reference or retained preparation. Changes
are limited to CLI, its typed Contracts decoder, the complete test inputs and decision record.
Compiler/type-inference/nullability failures, a function-length finding, the incorrect
503/500 fixture (228/229 passing) and the session negative control are not passing evidence.
The final runs contain no filter, skip, retry or unregistered partition.

Published CLI/process-signal acceptance, native Windows execution and the full Gate remain
unverified here; their required acceptance belongs to WP15, and later package inputs are not
implemented. No product version or changelog changed. Keep the pull request draft; WP10 is
next, with no WP10 edits begun.

The complete native PostgreSQL command, minimum 76 and otherwise the same Release/no-build/
no-restore/strict-zero/settings arguments, passes all 76 tests with zero failed/skipped,
172.479s. The three complete suites ran independently in parallel against unchanged inputs.
All owned ledger/identity fixtures and reapers have been removed by their owners. A read-only
container listing shows the same ten original evaluation container IDs, healthy and running;
none was connected to or modified. Logs are outside tracked source as `wp9-unit.log`,
`wp9-postgres.log`, `wp9-web.log` and `wp9-fsharplint.log`. `git diff --check` passes.
