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

## Implementation frontier

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
