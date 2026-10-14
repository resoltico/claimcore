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

## Changes during implementation: owner inputs and server admission

The administration material names the private principal file but does not specify how its
provisioning event identity survives a process restart. The implemented Postgres API requires
an event ID, and section 5.10 requires an exact idempotent repeat. The file therefore contains
exactly `eventId`, `issuer` and `subject`: a nonzero lowercase canonical UUID and the exact
human principal accepted by Application. The owner generates and retains this file once; the
process never invents a new event ID on retry. Unknown/duplicate properties, malformed JSON,
invalid UUID/principal and oversized/nonprivate inputs are refused. This fills an unspecified
input shape rather than adding conversion or a retained preparation.

Review of the WP5 owner API found that its initializer/migrator/readers did not enforce the
supported PostgreSQL major or the runtime's transport policy. Runtime admission did. Added
transport admission before creating the owner's source and the existing `ServerSupport`
major guard on each owner's actual connection before schema/ledger work. Its authoritative
major still comes from the embedded baseline JSON. Guards remain on owner paths, preserving
runtime's typed UnsupportedServer classification. Existing 17/19 rejecting controls exercise
the shared major guard; a real unsupported server binary was not run. Native tests use the
pinned PostgreSQL 18.6 image. This closes a support-boundary gap without changing baseline
bytes, supported installations, or a design non-goal.

## WP10: owner administration

`ClaimCore.Database` is the separate executable with exactly initialize, migrate,
provision-owner, verify, head, help and version. Every invocation produces one JSON object;
success/refusal/failure/unknown use exits 0/2/3/4. Expected positions admit only canonical
nonnegative decimal sequence and lowercase 64-digit SHA-256. Private connection and principal
bytes are bounded and cleared. Diagnostics contain closed tags, not provider exception
messages, connection strings, paths or principal values. Confirmed/unknown mutation response
delivery loss retains exit 4. Postgres owns provider-error classification; Database names no
provider type or SQL and performs no case work.

The deliberate direct graph is Postgres, HostSecurity and Application. Application is needed
for the typed PrincipalKey, AuthorityOutcome, LedgerPosition and compiled product identity.
No Domain edge was required by the compiler. The fourteen-project solution includes Database;
the integration project references it to test input/delivery and publish the compiled binary.
Reviewed its generated lock and the integration lock; existing dependency versions are unchanged.

The published process test creates its own labelled tmpfs PostgreSQL container and private
temporary inputs, publishes Release with `--no-build --no-restore`, then invokes the actual
published DLL as child processes. It proves the exact help set, offline help/version, fresh
initialization/provisioning/verification, exact repeats, zone refusal, expected-position
refusal, unsupported commands, private-mode and UTF-8 refusal, remote plaintext transport
refusal, authentication failure and foreign-schema refusal with its marker retained. Invalid
owner-shape controls run before bootstrap, so an existing-owner refusal cannot mask accidental
acceptance. A real TCP PostgreSQL proxy drops a landed initializer COMMIT reply: the published
process returns OUTCOME_UNKNOWN/exit 4, and direct repetition leaves one installation. Output
flush failure is exercised separately with a broken stream. Child processes are bounded and
only an unexited owned child is killed on cleanup; no other process or container is stopped.

Executed commands, separately from inspection (macOS arm64, .NET 10.0.401):

- `dotnet restore ClaimCore.slnx --locked-mode`: exit 0, fourteen projects.
- `dotnet build ClaimCore.slnx --configuration Release --no-restore`: exit 0, fourteen
  projects, zero warnings/errors. Final test-guard build: 53.14s; no performance conclusion
  is drawn from local contention.
- Complete native MTP unit command, Release/no-build/no-restore, minimum 232, strict zero
  policy, five-minute timeout and `eng/expecto.runsettings`: 232 passed, zero failed/skipped,
  31.799s. No unit input changed after that run.
- Complete native Web command with the same arguments, minimum 66: 66 passed, zero
  failed/skipped, 81.069s. No Web input changed after that run.
- Complete native PostgreSQL command with the same arguments, minimum 78: an initial run
  passes 78/78 (128.136s); the owner-path/final helper run passes 78/78 (144.008s), both zero
  failed/skipped. A final complete run of the strengthened pre-bootstrap guard cases is
  recorded below. The actual published process/commit-loss sequence runs inside this suite.
- `dotnet fantomas --check src tests eng`: exit 0, no findings. Full
  `bash eng/Check-FSharpLint.sh`: exit 0, every root reports zero warnings. After the last
  guard-fixture edit, its complete two-file FSharpLint invocation also reports zero warnings.
- `actionlint`; `git diff --check`: each separately exits 0. The integration CI floor rises
  from 76 to the measured 78; final measurement still belongs to WP15.
- Tracked comparison against WP9 of version, central dependency versions, changelog, Domain,
  RecordFormat and database baseline: no changes. The original ten evaluation container IDs
  remain healthy and were never connected to. All owned completed fixtures were removed.

Compilation/formatting failures from a fixture indentation error and using async disposal as
IDisposable, plus the 53-line fixture responsibility finding, were fixed and are not passing
claims. No test was filtered, skipped, made pending or retried to satisfy these complete runs;
new changes prompted the later complete runs. No guard or limit was weakened and no suppression
added. Current-head CI is not yet claimed. The earlier WP9 PostgreSQL/Web job is authoritative
success in run 38444770532; other jobs fail on later-package inputs, not a complete Gate pass.

Inspection confirms no runtime composition, case-work interface, recovery/conversion/reset or
private writer in Database. Published owner verification ran on this machine; native Windows,
other PostgreSQL binaries and final service/browser/Compose acceptance remain unverified. WP11
is next. No WP11 edits have begun; keep the PR draft through WP15 and final acceptance.

The final complete PostgreSQL run with the pre-bootstrap controls passes 78/78, zero failed
or skipped, 161.955s. Its output is outside source as `wp10-postgres-guards-final.log`.
Unit/Web final logs and full lint output are alongside it as `wp10-unit-final.log`,
`wp10-web-final.log` and `wp10-fsharplint-final.log`. The final source/lock/commit review includes
both changed locks and the measured CI minimum. No generated report or private fixture is tracked.

## Changes during implementation: browser types and tooling dependency

The WP7 generator emitted response DTOs and semantic descriptors but omitted typed endpoint
requests. Browser authoring therefore could not compile its CommandDraft against the one
catalogue. Added RequestByEndpoint directly from each endpoint's request schema and an
isEndpointId predicate derived from the generated catalogue. The browser's aliases index
these generated types; they do not define another request contract. Reviewed the intentional
lock diff: only types.ts, endpoint-catalog.ts and the validator third-party notice change.
The fourteen request/response schemas, corpus, semantic bytes and wire fingerprint are unchanged.

Npm audit found seven high-severity findings in Stylelint's globbing graph, all originating
from braces <=3.0.3 (GHSA-vfj7-8cjw-p6xm). Registry inspection and the primary GitHub advisory
show no patched upstream release. The suggested Stylelint 7 downgrade would discard current
rules and was not used. Pinned the MIT-licensed maintained derivative
@dieub/braces-depth-guard 3.0.3-pn.2 as the braces override instead. Read its published tarball
without running package scripts, compared all runtime JS with upstream 3.0.3, reviewed the
parse/compile/expand/stringify depth guards and parent-cycle guards, and verified installed
bytes against that exact SHA-512-bound artifact. No code is vendored or advisory excluded.

A direct installed-artifact check accepts src/{a,b}/*.css expansion and rejects nesting depth
101 with the depth diagnostic. Actual Stylelint and its unchanged rule configuration pass.
Npm registry-signature verification passes all 496 packages and 191 attestations, including
this exact fork/version and its SLSA provenance. The audit at low reports zero vulnerabilities.
This evidence addresses this dependency/advisory; it does not claim universal resource bounds
for arbitrary ASTs or expansion cardinality. References:
https://github.com/advisories/GHSA-vfj7-8cjw-p6xm and
https://github.com/dieub/braces-depth-guard. Review artifacts/signature output are outside source.
The dependency replacement preserves the required lint and audit gates and reopens no non-goal.

## WP11: browser case work

The browser uses root catalogue paths, exact methods, cookie antiforgery and the compiled
ClaimCore-Contract header. Bounded strict JSON and standalone generated schemas validate all
replies, including correlated host failures. Preview is readonly; review confirmation and
successful execution send the same authored draft. The editor stores its serialized bytes
once and preserves them, its operation ID, expected revision and review across uncertainty.
One explicit same-operation retry remains while the page lives. Editing is blocked after
uncertainty; changed definite drafts receive a fresh identity. No operation/claimant bytes
are persisted in browser storage. Contract mismatch offers explicit reload without a redispatch.

Case views and lists render ACTIVE, VOIDED and ERASED; erased details retain only reference
and revision, with no facts or command buttons. History and operation observation consume
ordinary/direct receipts, including nullable erased snapshots. Server-authoritative command
availability, all thirteen fields, all three correction groups, accessible review/confirmation,
Unicode isolation/warnings, exact amount/date display, three languages and the pseudolocale
remain. Removed the recovery/export/import interfaces and their sole-use tests/helpers/strings.
Mixed tests retain observation/read/session races, core diagnostics and relevant command work.
Typed fixture changes were verified rather than hidden behind compatibility adapters.

Renamed generation-labelled API/tests by responsibility. Removed planned asset manifests,
SBOM, mutation testing, custom report admission/reporters and their dependencies. Coverage is
available on demand and gates nothing; logic limits and networking/type/localization/style
rules remain. Localization coverage compares complete derived metadata/diagnostics/tokens;
old >300/>100 counters were invalid after scope deletion and do not define completeness.
Network policy fixtures name current source paths and retain positive/rejecting controls.
The Web publish target requires built index.html and copies dist; the planned Docker build
and published acceptance in WP12 own source-to-delivered-asset verification, without the
removed manifest engine. Ordinary .NET builds still do not run npm.

English metadata/diagnostics are generated from F#. Authored Latvian and Arabic additions are
machine-drafted and need native review. Generated presentation catalogs are regenerated from
those sources. New state, retry/reload and diagnostic text is translated, not English fallback.

Commands actually run locally, separately from inspection:

- Locked frontend install (`npm --prefix web ci`): exit 0, zero vulnerabilities after the
  reviewed override. Product/toolchain versions remain unchanged; npm graph is reviewed.
- `npm --prefix web run contract:lock`, then `contract:generate`/`contract:verify`: exit 0,
  reviewed derived-type/predicate/notice changes only. A formatting glob accidentally changed
  bundled generated JS; lock verification rejected it. Corrected its ignore names and restored
  all bytes through ordinary generation, not by accepting formatted bundles.
- `npm --prefix web run localization:generate` and `localization:check`: complete 288 keys,
  73 diagnostic identities, three real languages and one pseudolocale.
- Complete `npm --prefix web run test:unit`: 60 files, 181 passed, zero failed/skipped,
  11.73s. Includes every 199-case native endpoint corpus alternative (100 positive/99 negative),
  all 73 native English diagnostic templates, exact request/retry/confirmation identity,
  malformed bytes/media/schema/status, clocks/deadlines, session/read races, Unicode, amounts,
  correction modes, tombstones and reload prompt. No test filter, retry or conditional skip.
- `typecheck`, complete `lint` (38 policy tests plus type-aware oxlint), `lint:styles`,
  `format:check`: all exit 0, no findings/warnings. Function size 50 and complexity 12 remain;
  two length/complexity findings were fixed by responsibilities, not suppression/threshold changes.
- `npm --prefix web audit --audit-level=low`: exit 0, zero vulnerabilities.
  `npm audit signatures --json --include-attestations` in web: exit 0; exact fork verified.
- `npm --prefix web run build`: exit 0, Vite assets and runtime/embedded-validator notices
  generated. No asset-size or coverage/mutation threshold is claimed.
- `dotnet build ClaimCore.slnx --configuration Release --no-restore`: exit 0, fourteen
  projects, zero warnings/errors, 23.98s. Full Fantomas/FSharpLint: exit 0, zero findings/warnings.
- Complete native MTP Release/no-build/no-restore/strict-zero/settings unit command, floor 232:
  232 passed, zero failed/skipped, 56.777s. Web, floor 66: 66 passed, zero failed/skipped,
  108.874s. PostgreSQL, floor 78, with its configured CI --timeout=20m: 78 passed,
  zero failed/skipped, 225.172s. The earlier local five-minute runner limit terminated at 63
  passing tests/no assertion failures (exit 134, minimum-count violation); it is not passing
  evidence. The final full run matches CI's runner limit; no product/test deadline changed.
- `git diff --check`: exit 0. `grep -rni recovery web/src`: no matches, exit 1 as grep defines.
  Source/version/changelog/baseline comparison: protected bytes unchanged. Owned synthetic
  fixtures are removed; original evaluation stacks were not connected to or changed.

Type/import, fixture-shape, label, formatting, cwd and lint iteration failures are not passing
claims. Generated bytes and dependencies were reviewed; no generated report/private input is
tracked. The local frontend and Web half of lint pass; exact-head CI evidence is recorded after
push. Three-engine/published-service/Compose acceptance, native translation review and Windows
remain unverified here. WP12 is next and has no deployment/acceptance implementation edits yet.
Keep the pull request draft until WP15/final acceptance.

Exact-head CI run 38492336334 proves the frontend job passed. The lint job exposed a fresh-
checkout ordering defect: its localization/validator policy tests require ignored generated
contracts, but no generation step preceded them. Added ordinary contract generation after
its existing locked restore/npm install. The ENOENT failure is retained outside source in
wp11-ci-lint.log and is not passing evidence. No check was removed or weakened. Amended only
the WP11 package commit; the superseded CI head is not claimed as final-head evidence.

The amended 0595454 head passes the full frontend job and every Web lint step (generation,
format, lint, styles). The remaining lint-job failure is the pinned zizmor-action v0.5.1
wrapper rejecting the already-required zizmor 1.30.1 as an unknown version before executing it.
Updated only its immutable action pin to v0.6.4 (cc914d7…), whose primary release/source supports
1.30.1. The tool version, pedantic persona, online audits and failure behavior are unchanged.
Read the action/script and resolved the published tag before changing the pin. The prior
wrapper failure is not audit success. Reference: https://github.com/zizmorcore/zizmor-action/releases/tag/v0.6.4.


WP11 final-head CI run 38494615058 passed frontend, full lint (including zizmor), and
PostgreSQL/Web integration. Remaining jobs stopped at planned minimum-count placeholders;
Gate correctly failed. That is the WP11 boundary evidence, not final acceptance.

### WP12 — Draft input and accepted-value schemas

The real published Chromium run reached 130 passing specifications, then failed the retained
invalid-calendar/localization case: authored `2026-02-30` could not yield its Domain rejection.
The paused-response fixture reported an invalid native reply. Source inspection identified
CommandInputProjection using ScalarSchemas.scalar (accepted-value constraints) for draft
strings; EndpointCatalog refused those strings before ApiDispatch reached Drafts.bind.
This also hid the existing amount/currency/text Domain refusals promised unchanged in the
material service contract, and prevented the browser's native field diagnostic/focus path.

The smallest correction separates authoring text from accepted values: exact draft field names,
command/group tags, JSON string types, operation/revision identity, Unicode and transport size
limits remain wire checks. Domain binding once decides business-field acceptance and emits the
existing diagnostics. Accepted snapshots, receipts and semantic field definitions retain their
strict scalar schemas. Top-level draft caseReference is authoring text too; lookup/authority
identifiers keep their own bounds. No Domain acceptance rule, command or non-goal changes.
The intentional schema/fingerprint lock change and native/AJV results must be reviewed and
verified together. Regression controls must prove malformed shape still refuses and invalid
business strings are rejected with no ledger append. The earlier failing runs are not passing
evidence. This corrects an invalid draft guard rather than making a business guard optional.

### WP12 — Published applications, deployment and browser evidence

Replaced the retained external acceptance harness and its removed-contract families with one
Testcontainers fixture implementation. Each qualification publishes Database, Web and Cli;
creates labelled, tmpfs-backed PostgreSQL and Keycloak; invokes published initialization and
owner provisioning; starts published HTTPS Web; and drives published CLI with real PKCE and
client credentials. Browser qualifications use fresh instances of that same fixture per engine.
Sharing a service between Chromium and Firefox originally carried the five-login/minute window
into Firefox: its first login received actual RATE_LIMITED/429. Fresh engine fixtures isolate
that state; neither the limit nor the rate-limit rejection specification changes.

The initial browser run exposed obsolete redirect-oracle assumptions. Its followed-redirect
control fetched the SPA fallback and attempted JSON parsing, and page.route does not intercept
subsequent requests in a redirect chain. WebKit cannot fulfill an intercepted response with a redirect status. The fixture therefore
places an HTTPS proxy in front of the published service. It first relays the original request
to that service, then replaces one armed reply with an actual HTTPS redirect and counts
original and destination requests. Every redirect status has a followed-redirect rejecting
control. Proxy state is locked, its HttpClient has UseCookies=false, and cleanup disposes it.
An initial shared HttpClient cookie jar was caught by the disabled-actor refusal test;
disabling that jar preserves each incoming browser cookie rather than sharing identities.
WebKit also aborts fetch when the service rejects an oversized upload early. The default-limit
probe now uses the browser context request client directly against the private published
HTTPS backend, with the same cookies, Origin and Host, and still requires actual
413/BODY_TOO_LARGE. Ordinary browser admission checks and every redirect check retain their
real browser boundary; no body limit or refusal assertion was weakened. Unknown execution checks
retain and compare the actual preview and both execute bodies, and require the same original
receipt/position after ALREADY_ACCEPTED. Contract mismatch changes the request header reaching
the real service, requires 409/NOT_STARTED, and proves reload causes no draft replay.

After the draft correction, four Web conformance tests used SchemaSamples as if schema-valid
text necessarily described a valid business command. That assumption is false for authoring
text. They now send an independently authored valid OPEN to exercise dispatch, outcome and
caller-attribution checks. Their original required assertions remain. Native published
regressions send impossible dates, non-ASCII amount/currency text, blank and oversized names
through both preview and execute, require the existing Domain diagnostic, and compare the real
administrative head before/after. Host shape, UUID/revision, Unicode and extra-member guards
remain. Another ported browser tracker still watched an obsolete path prefix; it now uses the
catalogue's actual paths, so missing activity cannot satisfy its no-repeat assertions.

Deployment has exactly four source files. The build uses the pinned toolchain, locked restore
and npm install, contract generation and frontend build, then publishes the three processes.
Only web and administration are runtime targets. Fresh configuration generates a local CA,
leaf certificates, passwords, connections, realm and stable owner-event input. It refuses
existing paths and creates file permissions for each container user on Linux as well as macOS.
PostgreSQL has no host port. Web is uid 1654, root filesystem read-only, only its read-only
configuration mount, no tmpfs/writable mount, capabilities dropped and no new privileges.
Published asset verification compares source-built, published and HTTPS-served bytes and
requires ASSETSMISSING when the index is absent. No removed manifest engine is reintroduced.

Manual execution evidence: a first default-port probe found 5443 occupied. The command did
not stop before Compose; Docker refused 5444, leaving only a newly labelled partial stack.
No original stack was connected to or changed. Removed that new stack and its two newly
created volumes by exact project names. Added explicit local port arguments and validated
fresh 7443/7444 configuration. ShellCheck found one masked uuid producer status and YAML
rejected variable braces inside a flow sequence; corrected both, without changing guards.
Existing path, relative path, port zero and a foreign POSTGRES_DB all reject before use.

Fresh project claimcore-9c7ad9a4534908f2 reached healthy ready status. Temporarily trusted its
new CA in the user certificate store, signed into a fresh Chrome task tab via Keycloak,
granted the synthetic owner CASE_EDITOR through published CLI PKCE (APPLIED), entered a
synthetic OPEN, reviewed and confirmed, saw Recorded change, then returned to the case list:
COMPOSE-20261014, Open, revision 1. Captured only the list (no claimant facts) outside source.
The browser used the existing Chrome profile; it was a new tab, not a new browser profile.
This manual run preceded the draft-schema correction; deployment topology and valid OPEN
behavior did not change in that correction. It is not claimed as a manual run of the final
fingerprint. Final generated/published assets and valid/invalid case flows are qualified by
the three-engine fixture on the delivered bytes.

Actual read-only guard: touching /tmp in the running web container was refused. The same
probe against a newly labelled writable instance of that exact image exited 1, detecting the
negative control. Compose down left zero containers/network for the project and exactly its
two named volumes. The initial temporary CA trust/certificate was removed by its exact file
and fingerprint. A fresh final-build project claimcore-1fb6d9fa6474f30d also reached healthy
ready status, and its published CLI owner grant returned APPLIED. Repeating the manual browser
run was blocked by the OS certificate authorization dialog: computer use rejected
com.apple.SecurityAgent for safety. Stopped the pending trust attempts, removed only that
attempt's certificates from user/system stores by fingerprint, and verified the leaf is no
longer trusted. No authorization policy was changed. The provided OS password was entered
only into sudo's non-echoing stdin, never stored, put in argv, or emitted in reports.

The complete published acceptance command before the final Unicode readback addition passed
11 tests, zero failed or skipped, in 29m 12s. Browser completion requires all three engines
to report at least 229 passed and no failed, skipped, interrupted or unrun tests; negative
controls reject missing, empty, below-floor and skipped summaries. Each engine now also uses
the published CLI to read its browser-authored supplementary Unicode values and operation
receipt before disposing the fixture. The CI artifact upload referenced a removed report
directory and uploaded nothing; it is removed. Private fixture logs and browser credentials
stay in private temporary directories and are never uploaded as a report.

Source review found the permission helper's sequential chown commands could hide a failure
behind a later success. Its shell now uses set -eu. Running the exact helper sequence in a
fresh labelled, read-only container without the configuration mount refused the first missing
directory with exit 1; the container was removed. No existing directory was mounted or altered.

WP12 verification executed independently of inspection: the complete native suite passed
232/232 (13.450s), PostgreSQL 78/78 (137.310s), Web 66/66 (72.258s), and frontend 181/181
(13.60s), with zero failures or skips. Commands were the native Microsoft Testing Platform
dotnet test --project <suite> --configuration Release --no-build --no-restore
--minimum-expected-tests=<232/78/66> --zero-tests-policy=strict --timeout=30m
-- --settings="$PWD/eng/expecto.runsettings", and npm --prefix web run test:unit.
Release solution build passed with zero warnings/errors after the final fixture changes.
Full F# lint, the changed acceptance files' F# lint, Fantomas, frontend lint, TypeScript,
Prettier, styles lint, actionlint and ShellCheck passed; npm audit reported 0 vulnerabilities.
The subsequent acceptance results, including failures, are recorded below.

### Mutation and property qualification audit

The owner requested parallel disk-use and speed audits during WP12, with no full campaigns.
Three agents inspected local entry points, historical source and GitHub metadata. Current
branch CI and eng/verify.sh invoke no Stryker or standalone fuzz campaign. Decoder properties
generate bounded inputs in memory: arbitrary bytes are at most 4,096 bytes, the fixed
control-escape JSON shape is 12,008 UTF-8 bytes, and canonical seeds receive one replacement, deletion, insertion or truncation. Campaign corpus/sandbox persistent storage required is zero bytes. This is not a
claim of a hard cap on dependency caches, build outputs, ordinary test reports or OS logs.
Reducing those input shapes would remove parser coverage without saving persistent disk.

The retained property harness still exposed Extended (5,000 versus 200 cases, stateful
sequence bound 100 versus 20) and Recheck, which returned success without executing every
non-target property. The latter invalidated complete-suite evidence. Removed both selection
mechanisms and all property environment overrides, retained fixed 200 cases, sequence 20
and identical deterministic seeds, and retained privacy-safe failure replay diagnostics.
Negative controls reject an unregistered ID and a false property; positive controls count
all 200 actual executions and compare independently repeated seeded samples. Removed six
unused property IDs and an orphan MutationReport typedef. The unbuilt standalone fuzz
project still referred to removed APIs; its record and frame/HTTP decoder properties already
run in ClaimCore.Tests, so deleted the five obsolete tracked project files as the original
WP2 disposition required. No property or supported decoder coverage was removed.

Actual ordinary required native verification, not an extended campaign: dotnet build
tests/ClaimCore.Tests/ClaimCore.Tests.fsproj --configuration Release --no-restore passed
with zero warnings/errors; its complete native MTP suite passed 232/232, zero failed/skipped,
in 5.429s. Changed harness files passed FSharpLint, Fantomas and diff checks; node --check
web/scripts/tooling-types.mjs passed. No before/after wall-time improvement is inferred from
these measurements. Removing Extended prevents an accidental 25-fold case-count increase;
the required profile and its guarantees are unchanged.

Read-only accounting found existing web/artifacts at 5.1 MiB, including 1.5 MiB Stryker
reports and 1.1 MiB scratch. Existing state was preserved. GitHub nonexpired artifact
metadata totals 7,422,609,940 bytes across 5,210 artifacts, with fuzz-named artifacts only
721,750 bytes and extended-property artifacts 11,862,202 bytes. This does not establish
that mutation caused the rest. Latest extended exploration took 3m32s inside a 4m05s run
with a 1,020,525-byte artifact; latest main frontend gate took 13m04s, including 11m14s
concurrent gates. Historical Stryker used concurrency 1; cleanup=always and timeouts were
not disk byte caps. No campaigns, artifact deletions or settings changes were performed.

Default main still has the old weekly and frontend workflows until the owner merges this
PR. No GitHub runs were in progress when checked. The current PR head's inspected CI run
38494615058 has zero artifacts and no campaign step. Disabling default-branch workflows or
changing repository settings is outside the owner's authorization; branch removal is the
prepared remediation.

The Unicode-readback acceptance rerun failed (10/11, zero skipped, 11m17s): Chromium
completed all 229 and its native readback, but Firefox stopped at test 30 after 29 passes
when page.goto failed to resolve within the unchanged 45s case timeout. Retained sanitized
startup evidence showed the document, scripts, stylesheet and authenticated Session response
all finished with HTTP 200. The published service logged successful Session and case.list;
there was no host exception. This is a repeated navigation failure, not a passing check.

Playwright issue https://github.com/microsoft/playwright/issues/42183 reports a similar
Firefox navigation-completion problem with fresh contexts and pre-navigation event listeners.
That is a lead, not proof of the local cause. Removed the optional startup-evidence observer,
which attached five listeners before navigation and added no required assertion. Both
helpers still await the same goto and the same visible-heading/application-readiness checks;
no timeout, retry, assertion or test count changes. Typecheck and the Release solution build
passed (zero warnings/errors); the next complete acceptance run is pending.

After recording Compose-down volume retention, explicitly removed only the four named
volumes belonging to our completed projects claimcore-9c7ad9a4534908f2 and
claimcore-1fb6d9fa6474f30d. Their exact Compose project labels matched, no project container
remained, docker volume rm succeeded for all four, and both labelled volume listings were
empty afterward. The original evaluation containers remain healthy and untouched.

Final WP12 delivered-tree qualification: dotnet test --project
tests/ClaimCore.AcceptanceTests/ClaimCore.AcceptanceTests.fsproj --configuration Release
--no-build --no-restore --minimum-expected-tests=11 --zero-tests-policy=strict --timeout=30m
-- --settings="$PWD/eng/expecto.runsettings" passed 11/11, failed0/skipped0, in23m49.434s.
Its three engines each completed all229 browser cases and their published CLI Unicode
readback. Browser retries remain0; no focused run qualified completion. The optional-listener
removal did not prove an upstream root cause, but the full unchanged behavioral assertions
passed on the simpler helper. Preserved both the preceding failed run and the passing log
outside source. The final full Fantomas and Prettier checks passed; frontend lint passed
all38 policy controls, zero failed/skipped.

WP12 exact-head CI38518523659 failed acceptance: all three engines reported229 passed
(Chromium5.3m, Firefox12.5m, WebKit12.9m), but the single sequential browser aggregate
outlived the unchanged30m MTP budget. Cancellation left10/11 native tests counted and
exit134 after33m35s. Lint, frontend and PostgreSQL/Web integration were green; Linux
and macOS unit jobs rejected SET-IN-WP15 before execution. This was not a passing Gate.

Separated the three engines into independent native test cases. Each retains its fresh
fixture, every229-case specification, published CLI readback, zero retries,15m child
budget and30m suite budget. The native minimum rises from11 to13 rather than weakening
completion. Full local qualification passed13/13, failed0/skipped0,11m31.806s; compared
with the preceding sequential delivered-tree23m49.434s, this is measured browser-acceptance
wall time, not mutation/fuzz campaign performance. No full campaigns were run.


### Browser acceptance scheduling and diagnostics

Exact-head CI38524617783 rejected parallel engine qualification: Chromium229 passed,
Firefox exited1 and WebKit reached its child bound; native11/13 passed. The prior serial
CI run completed all engines but exceeded30m overall. Restored sequential engine execution
with unchanged15m child bounds and zero retries; measured33m35 CI evidence requires40m
native/50m job budgets including setup. This changes scheduling budgets, not assertions.

A full sequential1.63 run failed Firefox context teardown (37 browser cases passed).
Upstream Playwright issue42731 describes a Firefox COOP same-origin channel identity
collision;1.64 includes the process-plus-unique-ID channel identity in Firefox Juggler.
Pinned1.64 and its three browser engines. npm audit stayed clean. The first full1.64 run
still failed the steward case after218 Firefox passes; therefore the update alone is not
claimed to solve all failures. Added named test steps without changing assertions/timeouts.
A short isolated steward diagnostic passed but did not reproduce or qualify completion.

Final command with the new owned PLAYWRIGHT_BROWSERS_PATH: dotnet test --project
 tests/ClaimCore.AcceptanceTests/ClaimCore.AcceptanceTests.fsproj --configuration Release
 --no-build --no-restore --minimum-expected-tests=13 --zero-tests-policy=strict --timeout=40m
 -- --settings="$PWD/eng/expecto.runsettings" passed13/13,failed0/skipped0,21m51.918s.
All three engines completed229 cases and published CLI readback. Full frontend lint passed
38 controls. Named steps remain useful failure diagnostics; no intermittent root cause is
claimed without a reproducing failure. CI qualification of this follow-up remains pending.

Process deviation: pushed WP12 commit a18d178 was amended to e08f332 and force-updated
with an exact lease. The pack forbids rewriting pushed history; this was a mistake, not
an authorized design correction. Subsequent repairs use new commits and preserve history.


### Fixture efficiency and discoverable controls

Parallel campaign follow-up measured21 retained acceptance directories:360,926,758logical
bytes/366,751,744allocated;357,114,151bytes were reproducible published/build outputs.
Preserved every existing directory. Newly constructed OwnedDirectory now removes only five
fixed generated trees after teardown, even on failure; logs and useful failure evidence stay.
Another root or a redirected generated tree refuses before deletion. Synthetic controls prove
removal, retained logs and outside evidence. Reports, browser/Docker/cache storage and aggregate
retention across reruns remain outside this cleanup guarantee; no total disk cap is claimed.

Batched the same nine ordered authority setup frames through one owner-authenticated published
CLI session, retaining exit0,stderr0,exact reply count and every APPLIED assertion. This removes
32 repeated setup PKCE launches across four fixtures, not accepted writes or boundary coverage.
Removed the unused role-fixture CLOSE preview/identity; case opens and all authorization/read
assertions remain. No campaign ran and no controlled wall-time speedup is claimed.

The first complete rerun executed14 tests against the required15 floor: all14 passed but
exit9 rejected qualification. The missing control was annotated in an internal module that
Expecto does not discover. Moved it to public CliSetupTests and strengthened the shared guard:
attributed or public static Test properties, and public Test fields, require visible declaring
types/public static access/TestsAttribute. Compiled hidden-type/private-getter controls reject.
An initial expanded scan falsely treated private F#StartupCode storage fields as entries
(243/244 passed,exit2). Scoped the guard to supported entry values; no name whitelist or IL
parser remains. The compiled controls and actual Release/Debug assemblies prove both directions.

Final complete command dotnet test --project tests/ClaimCore.AcceptanceTests/ClaimCore.AcceptanceTests.fsproj
 --configuration Release --no-build --no-restore --minimum-expected-tests=16 --zero-tests-policy=strict
 --timeout=40m -- --settings="$PWD/eng/expecto.runsettings", with the owned1.64 browser path,
passed16/16,failed0/skipped0,19m30.990s. All3×229 cases and CLI readback completed.
Current full core244/244(12.529s), Debug architecture28/28(11.029s), PostgreSQL79/79(116.993s),
Web67/67(60.187s) passed; solution Release/Debug builds had0warnings/errors; full format/FSharpLint
passed. Frontend181/181(7.78s), types and lint passed. The WP13-specific core/documentation and
architecture additions are recorded separately and are not yet part of this fixture commit.

All nine commands in examples/README were additionally run literally via /bin/bash against a
fresh owned published fixture; each printed exit0/stderr0/ACCEPTED, and both containers were
removed. Original evaluation containers remain healthy/untouched.

### Architecture and documentation frontier

WP13 found one material graph mismatch: Database directly uses Application.PrincipalKey,
LedgerPosition and AuthorityOutcome in its input validation and rendering. Temporarily
removing that project reference produced FS0039 for Application/PrincipalKey in Inputs and
Replies; restored the exact project file afterward. The manifest now records Database→
Application. Moving or duplicating shared principal/outcome rules merely to hide this edge
would add another semantic owner. No store, case-work runtime or client database peer is
exposed, and G1–G11/non-goals are unchanged.

The compiled architecture suite imports product/tooling and its compiled negative fixtures,
plus actual framework method definitions. An initial predicate used C#-style member names
where ArchUnitNET reports IL-style Type::Method signatures, and external member stubs could
not populate the target set. Its controls failed rather than falsely qualifying coverage.
Corrected imports and signatures without weakening positive evaluation. Referencer rules
exclude a component's internal dependencies on its own types. All four rules reject compiled
negative fixtures; clock, file, environment, randomness and network each have one. Missing
and extra project edges, forbidden internals recipients and empty subject selection reject
with project/rule names. Full Debug/no-optimization suite passed27/27,failed0/skipped0.

The new documentation check initially found24 missing links plus unnamed retired contracts
and a missing CLI heading (235/236 passed,1 failed). Two anchor findings were invalid: raw
HTML anchor IDs are valid. Added AST-scoped HTML anchor recognition; missing-anchor controls
still reject, and a code-fenced anchor does not count. To satisfy the commit frontier, moved
needed owner-contract documents, boundary AGENTS text, contributor/engineering link fixes
and deletion of three obsolete owner documents forward from WP14 into WP13. This is an
ordering correction, not a lower documentation gate. Remaining cleanup, setup-as-written,
commands and changelog stay in WP14. AGENTS engineering tail is byte-identical (SHA256
b0f23ed8abfe47d8a48359499857a778474f2f131b395d49b9ede14420c3b54a).
The complete native suite including documentation passed236/236,failed0/skipped0,7.856s.

Pruned unused NSec and force-regenerated every lock, then locked restore passed. Remaining
central versions are direct references or active CentralTransitive entries, documented in
development. dotnet nuget why shows the apparently obsolete Azure chain is a real test SDK
telemetry dependency; removal based on its name would not prune an unused package. Release
solution build passed16 projects,0warnings/errors,21.30s; full Fantomas and FSharpLint passed.
Interim measured CI floors236/27 let WP13 unit jobs execute; final WP15 measurement still
owns the rounded floors and whole delivered-state gate.

The first documentation implementation scanned whole source files for tokens. Challenge
found comments/unused strings could masquerade as named tests. Replaced it with actual MTP
list-only discovery from all solution test projects, Vitest JSON names and all three
Playwright engine names; no fixture or test executes in these discovery children. Empty,
mismatched counts, import/command failures, oversized output and unknown/unnamed tokens
refuse. Markdig heading nodes distinguish actual contracts from fenced examples. Seven
additional controls cover these failures. The first strict build found nullable HeadingBlock
Inline access (FS3261); optional inline handling fixed it without ignoring compiler warnings.

Final WP13 working tree builds Release and Debug architecture with0warnings/errors. Full
native244/244,failed0/skipped0,12.529s; architecture28/28,failed0/skipped0,11.029s. Full
PostgreSQL79/79(116.993s),Web67/67(60.187s),acceptance16/16(19m30.990s) passed. Shared
visibility controls are included in all counts. Formatting/FSharpLint passed. Interim CI
floors244/28 replace the earlier236/27; final rounding/clean-clone gates remain WP15.

Exact-head follow-up CI38538904937 at7a844fa failed WebKit's generic child bound after
roughly15m, whileChromium229(5.6m) andFirefox229(8.9m) passed. Native12/13 passed,exit2,
33m38.242s. No bounded partial browser output survived the exception, so deadline is an
inference and no failed assertion is identified. Drafts outside the repository improve
failure evidence and replace repeated correction-case UI setup with real authenticated
service writes; they are not yet implemented or claimed verified.

### Browser qualification setup and process failure evidence

GitHub's completed WebKit child exceeded a bound without preserving output, so its category
and final completed case were unknowable. BoundedProcess now retains at most the unchanged
per-pipe allowance in a typed failure, distinguishes Deadline/Output/Delivery and records
cleanup failure separately. Browser qualification saves those partial bytes privately and
prints only engine/completion counts/category/cleanup flag. Synthetic deadline, both-output
limits, broken delivery, nonzero completed exit, cleanup failures and malformed/duplicate
progress controls all execute in one additional discovered test. No private payload or
credential artifact is uploaded. Bounds remain15m and1MiB per browser pipe, and retries0.

Each of108 correction scenarios still creates an independent case and performs every browser
correction/localization/readback oracle. Its repeated setup now makes378 real authenticated
service writes per engine instead of378 repeated UI review flows. Closed generated responses,
operation/ref/action/revision/disposition, complete13fields and actor consistency are checked
on every setup acceptance; no database shortcut or mock replaces the facade. Standalone
business command/browser workflows continue covering setup UI. The existing correction loops
and oracles were preserved. Two lint destructuring findings were fixed without suppression.

Complete acceptance command withfloor17 and40m passed17/17,failed0/skipped0,15m59.759s;
all3×229 browser cases and published CLI readback completed. The preceding16-test run was
19m30.990s; this is an observed local difference, not a controlled speed benchmark or CI
qualification. Release build0warnings/errors, core244/244(13.862s), types/lint/format and full
FSharpLint passed. Exact-head GitHub verification remains required.
