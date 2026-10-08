# CI governance and PR publication

This document owns workflow integrity, publication handoff and the scoped repository-settings
procedure. [Development](development.md) owns build/test commands; [Releasing](releasing.md)
owns release publication; [Owner review](owner-review.md) owns the sole-owner merge boundary. CI is
execution evidence, not independent owner approval.

## Verification

`ci.yml` covers PRs, merge groups, main pushes, version-tag pushes and manual runs. It calls one reusable
workflow per verification family and ends in the unique aggregate `Gate` job, whose `needs` list is the list
of families: a family added to `ci.yml` is gated automatically, and a failed, skipped, cancelled or omitted
family fails the Gate. PR and merge-group runs may cancel superseded work; their concurrency groups are
separate from main/tag push and manual events.

Verification is job-local. Each job verifies what it produced against committed inventories before uploading
(test reports, frontend reports, coverage reports) and each consumer of published bytes verifies the manifest it
received. There is no cross-job reconciliation record; GitHub binds artifacts to the run, and the release path
reads only the Gate conclusion. The cross-platform suite matrix is derived from `config/test-suites.json` by a
`plan` job, so a new suite or platform needs no workflow edit.

Use **Re-run all jobs** after an infrastructure failure. The supported unit is a complete attempt, not an
arbitrary mixture of successful jobs from previous attempts. Do not select a vaguely "latest successful"
artifact or lower a check to make a partial rerun green.

## Workflow policy

Two independent layers check every workflow and the composite toolchain action:

- **Industry tools**: actionlint for syntax and expressions, and zizmor (pedantic persona, with GitHub-backed
  audits when a token is present) for pins, credential persistence, permissions, dangerous triggers, template
  injection and the rest of its audits. The only disabled audit, `self-repository`, is registered in
  [`config/lint-exceptions.json`](../config/lint-exceptions.json).
- **Repository policy** (`node eng/ci/check-workflows.mjs`, tested in `eng/ci`): rules specific to this
  repository. It parses the YAML with the locked YAML 1.2 parser; duplicate keys, aliases and merge-key syntax
  are rejected rather than partially interpreted.

The repository policy requires literal full-commit action pins with a reviewed version comment, checkout without
persisted credentials, toolchain selection only through `.github/actions/toolchain`, read-only workflow and job
permissions except in the publisher (main-only, `release` environment), pinned runner images, no privileged PR
events, every reusable verifier reachable from `Gate`, and the upload guard: every job that uploads artifacts has
exactly one `artifact_scan` step (`node eng/ci/scan/main.mjs artifacts <paths>`) immediately before its first
upload, running with `if: always()`, every upload conditioned on its success, nothing else running after it, and
every uploaded path inside a scanned path. Comments cannot stand in for executable settings.

These are consistency controls under source review, not a security sandbox against an actor who can rewrite
both the workflow and its checker. Repository authorization is a separate boundary.

## Failures

Each stage and test process prints its output as one group in the job log, and a failing stage's output is always
shown. Schema-admitted, scanned diagnostic evidence may upload from an otherwise failed job through
its deliberate `always()` admission path. That preserves failure visibility and does not make Gate pass.
Artifacts are scanned after production and before upload; a missing path, scanner failure or detected
secret prevents upload. Source review and passing settings-helper tests do not activate native protections.

## PR publication and handoff

Publish on one feature branch and open/update the PR. A local commit, downloadable patch or compare
URL is not publication. Resume existing work rather than duplicate branches or PRs. Temporary
source-transfer infrastructure is exceptional: announce its purpose, use minimal permissions,
never export credentials, and remove it from the submitted tree. Disclose remaining preparation
commits. Do not merge or change protections as a side effect of ordinary PR publication.

Read back the actual PR head, base and checks with GitHub itself (`gh pr view`, `gh pr checks`): a successful
result is the PR-triggered workflow's `Gate` for the current head. Use the
[PR template](../.github/PULL_REQUEST_TEMPLATE.md) and update the handoff after fixes. Prefer links to live
checks over stale repeated status in a long description. Identify draft status, actual head, qualifying
run/attempt and any limitations independently; "PR created" and "CI passed" are different claims. Never switch
untrusted-code verification to privileged `pull_request_target`.

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

The reviewed policy requires PRs and resolved review threads while retaining strict GitHub Actions Gate,
non-fast-forward and deletion protections. New PR rules require zero approving reviews to avoid impossible
self-approval; stronger pre-existing review rules are preserved. Branch cleanup and update-branch UI use native
repository flags, not custom branch-deletion automation. Auto-merge is disabled. A separate update-only ruleset
allows only the numeric repository owner User to merge through a PR; its bypass never applies to the independent
Gate ruleset. Wider, hidden or incompatible existing owner rules require reconciliation, not automatic
replacement. Version tags become immutable against update/deletion; tag creation remains with existing content
writers and release publication remains separately reviewed.

Update-rule readback accepts GitHub's exact parameter-free representation or an explicit
`update_allows_fetch_and_merge: false`. Empty, null, unfamiliar or permissive parameters are refused;
scope and bypass identities remain checked independently.

The publisher references the `release` environment. The settings plan adds a main-only deployment policy and an
explicit owner reviewer when absent. Existing reviewers and waiting/self-review rules are preserved, not weakened. Duplicate protection
rule types, missing rule information, malformed timers/self-review settings, and invalid or duplicate
reviewer identities refuse the plan before writes rather than being coerced or silently selected.
The initial sole-owner policy allows approving one's own manual publication job; this is explicit owner
authorization, not independent review. Ambiguous or inherited rules, existing bypass actors and unfamiliar
environment restrictions require owner reconciliation.

The helper checks only its declared flags, rules and environment, including the separate owner-only PR-update rule.
It binds the owner identity as well as the repository and proposed operations. It does not certify classic branch
protection, all Actions execution-policy settings, credential scopes or identity separation; those require separate
owner inspection in GitHub. Source tests use mocked GitHub responses and are not evidence that live settings were
applied.
