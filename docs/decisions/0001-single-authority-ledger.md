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

## Implementation frontier

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
The next step is WP2: replace lifecycle/erasure/hold Domain types with Stewardship, preserve
the nine business commands, restore the Domain-only test project and prove the three test
discovery guards with negative controls.
