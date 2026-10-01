# Repository tooling design

This decision record covers the eleven proposals in the repository-tooling brief and the adjacent
failure modes found during source review. Existing configuration is evidence of current behavior,
not a constraint on this design. Implementation follows a separate adversarial design review.

## Objective and boundaries

Verification must say exactly which source, commands, platforms and tests it checked. Missing
tools, incomplete selection, stale outputs and process failures must never become a passing check.
The smallest useful shared foundations are source enumeration, executable selection, task
scheduling and suite requirements. Product behavior, persistent record formats and owner-side
backup protocols do not need to change to establish these foundations.

The design does not promise that future extensions will never require refactoring. It makes the
current responsibilities explicit, keeps extension points narrow and gives their failure semantics
executable negative controls.

## Source inventory and clean execution

One Node source-inventory module owns enumeration for engineering source checks. Git supplies
tracked and untracked files, using repository `.gitignore` rules rather than personal/global
exclusions. Force-tracked ignored files remain visible. Deleted tracked files are removed; duplicate
Git listings are collapsed; case collisions, path escapes, links and nonregular entries are refused.
Names use NUL framing throughout, including change detection. Plain-directory fixtures use an
isolated Git database, never a newly initialized working tree. An invalid existing Git marker is an
error rather than permission to fall back.

Consumers select paths by responsibility, not by a universal language taxonomy. Ignore-policy
probes and generated/artifact scans remain different inventories. Filesystem inspection is valid
for verifying a selected file, an archive or an artifact tree. A blanket ban on traversal is rejected.
The F# Markdown tool retains its parser and exact-case path validation; shared semantics do not
require making its source-enumeration boundary depend on Node.

A clean-source check copies the current visible working tree, including uncommitted/untracked
source, into private temporary storage, then performs locked solution and qualification restores
and builds registered suite prerequisites. It never copies ignored build state or private `.local`
files. It verifies source fingerprints before and after copying so concurrent edits cannot silently
qualify a different tree. Result roots and cleanup paths are validated inside the ignored artifacts tree before mutation;
link traversal and cleanup of the tree root are refused. The check is a preflight, not complete test execution.

Workflow-reference validation checks executable source references in parsed workflows, composite
actions, stage plans and the local plan. It rejects missing literal engineering entry points and
local workflow/action references. Generated artifact paths and shell prose are not source references.
Dynamic references cannot be certified by a literal-path check; they remain subject to execution and
the existing workflow graph checks.

## Processes and pinned tools

An executable-selection module centralizes platform differences. Windows archive extraction uses
system bsdtar; Node children use the current Node executable; .NET honors an explicit SDK root;
Windows npm/npx invocation uses their JavaScript entry points without a shell. Git remains selected
from PATH but is run with redirection variables scrubbed for repository inspection.

Process wrappers retain responsibility-specific output handling. Secret-bearing drills do not gain
an automatic command-line or stderr dump. Startup failure, exit failure, signal termination and
timeout are distinct failure cases, and each wrapper settles once and closes owned resources once.

Pinned-tool acquisition verifies the archive digest and the selected archive member. Installed-tool
records also bind the executable bytes; an edited executable or malformed marker is reinstalled,
never trusted. Concurrent installation uses independent temporary publication files. Acquisition
tests cover digest rejection, tampering and actual archive extraction; a three-platform CI smoke
step exercises the real tools. No dependency cache or cross-OS binary transfer is introduced.

## Task and suite model

The existing task scheduler becomes the common scheduling primitive for stages, local jobs and
suite processes. Plans remain small data documents with domain-specific metadata. A universal
plugin engine or a second handwritten task catalogue would add indirection without a new guarantee.

`after` remains an ordering edge, not an implicit requirement for successful predecessors: some
independent diagnostics should still run after a failure. Fail-fast stops new work and reports it as
not started. Task results use one explicit status (`passed`, `failed`, `skipped`, `not-started`)
rather than independent optional booleans; malformed callback results become failures. Plan metadata
is validated before execution. Skipped and not-started tasks are counted separately from passed tasks. Missing required
tools fail a stage on developer machines as well as in CI. Explicit local selection/scoping remains
partial verification, visible in the report, and cannot qualify the complete gate. Concurrency must
be a positive integer in every entry point.

Suite CLI parsing validates flags, values, suite IDs and groups before any build or execution.
Selecting a non-.NET suite through the .NET entry point is an error, not a zero-suite green result.
Unknown inventory selections are errors too. Registry validation rejects duplicate projects,
inventories, platforms and partitions and malformed paths, timeouts, environment and build metadata.

Suite prerequisites are explicit build projects and authoritative environment templates in the registry. The
runner restores and builds the transitive project graph of those inputs in locked mode; a repository
preflight separately verifies the complete solution. Architecture-report paths and CLI paths are
data rather than inferred from Debug configuration or the `postgres` group. Builds use one MSBuild worker with build servers disabled,
and the clean preflight runs exclusively. Measured binary copies belong to their own result directories
and use the selected build configuration. Discovery/report
formats remain concrete adapters: MTP discovery, Vitest run summaries and Playwright listing have
different semantics and should not be hidden behind an interface that falsely promises identical
capabilities.

PostgreSQL remains one CI family with internally concurrent registered suite/partition processes.
It already bounds concurrency and isolates measured outputs. A job per partition would multiply
SDK/package/tool setup and artifact plumbing without changing correctness. This is a final topology
choice, not postponed matrix work. Parent inventory equality and partition disjointness remain
required; partitions are execution units, not separate sources of truth.

## Assurance and review

Every required CI family continues to run for every qualifying source. Mutation execution remains
required per PR; source-only scoping and weekly substitution are rejected because tests, dependencies,
generated contracts and compiler configuration affect the result. Local scoping is convenience,
not reusable merge evidence. The no-cache convention is retained because removing redundant restore
work is sufficient here; it is not an architectural prohibition against all future caching.

Zizmor findings must produce a failing stage: remove `--no-exit-codes` and select the documented
pedantic persona explicitly. Add a negative control against failure-suppressing security arguments.

Contract-to-test visibility is derived from registered inventories. An orphan inventory cannot
satisfy a contract's evidence requirement. The document parser remains the owner of valid contract
declarations. Render a review map with contract IDs, suite identities and exact named tests, without
claiming the map proves assertion meaning. Blanket domain/store/wire quotas are rejected: contract
responsibilities vary and copying tags across layers encourages superficial evidence. Semantic
review still reads the contract and its assertions; deleting the last registered tagged test fails.

Generated stage documentation comes from stage-plan arguments and pinned-tool documentation from
the tool manifest. Authored explanation stays authored. Image equality remains an explicit invariant
between standalone Compose and the baseline: requiring a generated environment file would make
ordinary Compose startup depend on an engineering setup step for no additional safety property.

Contract locks require a nonempty canonical file list with unique portable names, valid digests
and byte lengths. Verification compares both digest and length; a duplicate entry cannot disappear
through conversion to a map. This strengthens the existing lock format without changing wire bytes.

## Language and governance decisions

Retain the F# docs tool. Markdig parsing, anchor/contract validation, safe paths and generated help
already form one tested responsibility; changing languages merely to remove two projects would
replace working code and add a second parser ecosystem. Retain the contract generator as one
engineering executable referenced for its pure corpus code: an executable assembly is referenceable,
and a thin wrapper plus another library would increase the project graph without changing the
runtime trust boundary. There is no compatibility shim to remove.

Retain Python for owner-side backup, signing, archive inspection and deployment qualification.
These are protocols with private-file, canonical encoding, replay, bounded extraction and custody
semantics, not just orchestration helpers. Porting them to JavaScript would enlarge the rewrite and
lose the current typed data models without eliminating Python: zizmor and the Python AST limit
checker also use the locked environment. Retain Bash for the concrete Docker/OIDC harnesses; the
shared Node scheduler is not a substitute for supervised fixture ownership. Existing exact-label
cleanup controls and sensitive-output canary controls stay required. No shell or Python port is
left pending by this decision.

Keep the read-only-plan / explicit-apply / readback settings helper. A manual alternative would
remove concurrency and partial-write checks from the executable path. No workflow receives owner
administration credentials. Live protection qualification is an external owner activity; source
tests must continue to describe themselves as mocked evidence. Do not install a scheduled drift job
with a token that cannot see private rule/environment state and then label absence as conformance.
Repository policy, thresholds and suppression accounting remain reviewable source controls rather
than claims of tamper resistance. Remove stale documentation about retired review hashes.

## Completion criteria

All eleven brief items have a decision above. Accepted changes are implemented and tested in this
work; rejected alternatives are recorded with their costs and consequences, not deferred.
Engineering tests, strict type/lint/format checks, exception accounting, workflow policy, docs
verification, generated inventories and affected native suites qualify the implementation. Full
cross-platform and database/browser results are only claimed after those commands or CI jobs run.
Publish the branch/PR without merging or changing live repository protections.
