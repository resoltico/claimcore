# CI governance and PR publication

This document owns workflow integrity, publication handoff and the scoped repository-settings
procedure. [Development](development.md) owns build/test commands; [Releasing](releasing.md)
owns release publication. CI is execution evidence, not independent owner approval.

## Design and separate QA record

The design was reviewed against merged baseline `b18b380f28bbf7943799aa137342aaa71c5c114f`
before implementation. Keep the modular verification graph, but give each evidence stage one
producer, retain producer identity during transfer and separate upstream freshness from security.
A source-review hash detects drift; it cannot authorize the changes that recompute that hash.

Separate design QA required rejection of duplicate records before flattening, exact run/attempt
identity, both YAML extensions, actual settings rather than comments, and mandatory-job reachability.
It rejected unconditional approval bots, elevated execution of PR code, automatic dependency edits,
unbounded/raw failure logs and required self-approval for a sole-owner repository. Implementation QA
also identified failure-permissive or skipped Gate steps and widened publication conditions; the
structural controls reject these even when the expected script text remains present.

The chosen authorization model is explicit owner merge and publication review. If agent and owner
use the same account, that cannot provide independent identity separation. A distinct automation
identity and independent human approval require a separate deliberate credentials arrangement;
this source tree does not fabricate that arrangement. This implements the already-agreed governance
package, not an additional release package.

## Verification and evidence ownership

`ci.yml` covers PRs, merge groups, main pushes, version-tag pushes and manual runs. All required
families feed both evidence reconciliation and the unique aggregate Gate. A failed, skipped,
cancelled or omitted required family must not qualify. PR/merge-group runs may cancel superseded
work; their concurrency groups are separate from main/tag push and manual events.

`StageDefinition.Producer` owns the artifact producer mapping. The evidence collector downloads
`claimcore-stage-*` artifacts without merging their directories. It checks the artifact name,
manifest filename, registered stage, run and attempt before exclusively publishing any manifest
into the reconciler's stage directory. Duplicate stages, wrong owners, unexpected entries,
symlinks, stale attempts and existing destinations are refused. Reconciliation-owned stages cannot
be supplied by downloaded artifacts. Existing source/lock hashes, output digests, exact test
inventories, browser results and coverage verification still qualify the complete evidence set.

Use **Re-run all jobs** after an infrastructure failure. The supported evidence unit is a complete
attempt, not an arbitrary mixture of successful jobs from previous attempts. Later incomplete
attempts explain this directly. Do not relabel older artifacts, select a vaguely “latest successful”
artifact or lower a check to make a partial rerun green.

## Structural workflow policy

`eng/Check-WorkflowToolchainPolicy.ps1` delegates to the locked YAML 1.2 parser and structural
controls in `eng/ci`. Workflow `.yml` and `.yaml` files and both composite metadata names are covered.
Duplicate keys, aliases and merge-key syntax are deliberately unsupported rather than partially
interpreted. The parser is an explicit development dependency at its already-locked version.

The policy checks literal action pins, real credential-persistence settings, centralized toolchain
selection, main-only environment-protected publishers, read-only verification permissions, upload
scan guards and the complete mandatory graph. A new required verifier cannot remain disconnected
from Gate or reconciliation. Comments cannot stand in for executable settings. The bootstrap
artifact checker and actionlint remain independent additional checks.

These are consistency controls under source review, not a security sandbox against an actor who
can rewrite both the workflow and its checker. Repository authorization is a separate boundary.

## Safe, actionable failures

Frontend, source-quality and publication wrappers retain a bounded report per stage under
`artifacts/diagnostics/<producer>/`. Procedure failure and evidence failure have separate exit
fields. Fixed guidance names the reproduction procedure; compiler locations are included only
when they match tracked source files. Unknown paths, free-form provider output and raw logs are
not published. Reports are scanned before upload and summarized in the workflow UI. Failure of
reporting does not turn a failed procedure or manifest into success.

Dependency reports expose only package identities and current versions verified against committed
locks, stable version alternatives, fixed result categories and bounded findings. Vulnerability
and deprecation checks remain required, including transitive NuGet packages; npm audits,
signatures, licenses and locked restores remain required too. Approved hold ownership and expiry
are checked without treating a newly published upstream release as an unrelated PR defect.

The daily, read-only, main-scoped `dependency-health.yml` reports available updates separately.
It does not change the dependency graph or open/merge PRs. Unheld updates fail that maintenance
workflow with an actionable report; missing, malformed or unavailable metadata is a distinct
failure, not a clean bill of health. Only explicit transient metadata failures receive bounded
retries. Maintainers monitor its failed runs and keep weekly Dependabot updates actionable.

## PR publication and handoff

Publish on one feature branch and open/update the PR. A local commit, downloadable patch or compare
URL is not publication. Resume existing work rather than duplicate branches or PRs. Temporary
source-transfer infrastructure is exceptional: announce its purpose, use minimal permissions,
never export credentials, and remove it from the submitted tree. Disclose remaining preparation
commits. Do not merge or change protections as a side effect of ordinary PR publication.

Read back the actual PR head, base and CI identity. From an authenticated workstation, the read-only
helper is:

```text
node eng/ci/pr-status.mjs --repository resoltico/claimcore --pr <number> --head <full-head-sha>
```

It requires `GH_TOKEN`, performs no writes and distinguishes absent/pending/approval-required,
unsuccessful and verified current-head checks. A successful qualification requires the PR-triggered
workflow, its current tested merge revision, exactly one successful Gate and a complete successful
current attempt. A changed PR, run or attempt during read-back is refused. It does not infer human
approval, bypass permissions, or substitute a manually dispatched head-branch run for PR checks.

Use the [PR template](../.github/PULL_REQUEST_TEMPLATE.md) and update the handoff after fixes.
Prefer links to live checks over stale repeated status in a long description. Identify draft status,
actual head, qualifying run/attempt and any limitations independently; “PR created” and “CI passed”
are different claims.

For Actions job-token publication, inspect the actual trigger state: PR opened/synchronize/reopened
events may require workflow approval, while a job-token push does not itself trigger another push
workflow. Human/app publication and fork execution approvals follow their own authorization. Never
switch untrusted-code verification to privileged `pull_request_target` to avoid that boundary.
See GitHub's [trigger rules](https://docs.github.com/en/actions/how-tos/write-workflows/choose-when-workflows-run/trigger-a-workflow).

## Source review versus owner authorization

The semantic review registry uses schema 2 and `source-reviewed`. Old `approved` records and the old
registry schema are not accepted as compatibility fallbacks. Evidence explicitly reports
`ownerAuthorization: not-assessed-by-ci`. A reviewed source hash and green execution reports do not
establish an independent reviewer or permission to merge. No business/wire/storage format changes
are made by this internal tooling change.

## Native repository settings: plan, apply, read back

A PR cannot activate GitHub repository settings merely by containing a declaration. The explicit
administration helper runs from the owner's authenticated workstation, not a product workflow.
Supply an existing narrowly scoped credential through `GH_TOKEN`; do not paste a token into chat,
source, artifacts or workflow inputs. Read access is required to inspect the relevant settings;
applying them requires repository Administration write permission and the relevant environment access.

```text
node eng/ci/repository-settings.mjs --repository resoltico/claimcore
node eng/ci/repository-settings.mjs --repository resoltico/claimcore --apply --plan-sha <reviewed-plan-sha256>
node eng/ci/repository-settings.mjs --repository resoltico/claimcore --check
```

The default command is a read-only plan bound to repository identity and exact proposed operations.
Apply requires that reviewed hash, re-reads before each write and verifies the resulting state.
It never retries a possibly completed write or rolls back over concurrent edits. Configuration is
not a multi-operation transaction: on partial failure, inspect confirmed operations and obtain a
fresh plan. An authorization error is not interpreted as missing configuration.

The reviewed minimal policy requires PRs and resolved review threads while retaining strict
GitHub Actions Gate, non-fast-forward and deletion protections. New PR rules require zero approving
reviews to avoid impossible self-approval; stronger pre-existing review rules are preserved. Branch
cleanup and update-branch UI use native repository flags, not custom branch-deletion automation.
Version tags become immutable against update/deletion; tag creation remains with existing content
writers and release publication remains separately reviewed.

Both publishers reference the `release` environment. The settings plan adds a main-only deployment
policy and an explicit owner reviewer when absent. Existing reviewers and waiting/self-review rules
are preserved, not weakened. The initial sole-owner policy allows approving one's own manual
publication job; this is explicit owner authorization, not independent review. Ambiguous/inherited
rules, existing bypass actors and unfamiliar environment restrictions require owner reconciliation.

The helper checks only its declared flags/rules/environment. It does not certify classic branch
protection, all Actions execution-policy settings, credential scopes or identity separation.
Those require separate owner inspection in GitHub. Source tests use mocked GitHub responses and are
not evidence that live settings were applied. Do not record this package as operationally activated
until the apply/read-back results and those owner checks are available.
