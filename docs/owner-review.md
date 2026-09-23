# Owner review and merge authorization

This document owns the sole-owner authorization boundary. [CI governance](ci-governance.md) owns
workflow integrity and the settings procedure; [Development](development.md) owns verification.
The policy is for a personal repository with default branch `main`. Organization ownership or a
merge queue requires a separately reviewed policy, not an automatic extension of this one.

## Design before implementation

The reviewed source baseline is `bf439ebbde15166ef889377186c0813a6ed6ac6f`. PR #24 already makes
source review distinguishable from approval and supplies explicit settings administration. This
package completes that boundary rather than adding a second approval registry or workflow.

Three independent facts remain explicit: CI tested a particular source, an agent reviewed a source
subject, and the repository owner decided to merge particular revisions. No CI result, review hash,
PR author checkbox, comment command, label, or agent declaration establishes the last fact.

Use two native rulesets on `main`. The existing verification ruleset requires strict Actions `Gate`,
a PR, resolved review threads, no deletion and no non-fast-forward update, with **no bypass actors**.
A separate update-restriction ruleset permits only the repository owner's numeric **User** identity
to bypass that update restriction, and only through a PR. It contains no status-check exception.
GitHub combines applicable rulesets: the owner still must satisfy the separate verification rules.
New PR rules require zero approving reviews; stronger pre-existing review rules are not weakened.
Disable repository auto-merge so a deferred automatic merge cannot stand in for a decision on the
current head. No merge bot or new privileged PR workflow is introduced.

A read-only owner-review report binds repository ID, owner ID, PR number, base/head commits, tested
merge commit and tree, reviewed tool revision, and current CI run/attempt. It compares the full base
and tested-merge Git trees, not the PR description, labels, a contributor-authored checklist, or a
possibly truncated patch list. Every changed path requires owner review. Architecture, security,
contract-policy and general scopes highlight review questions; they are never exemptions. Removals,
renames represented as removal/addition, file modes, symlinks and submodules remain visible.

The report executes only the locally reviewed tooling; candidate source is fetched as metadata and
never checked out or executed. Both trees must be complete with distinct validated entries. It
re-reads PR and CI identities and fails if they change. It never creates approval, changes settings,
merges, or claims that reading a report authorizes anything. Reports are private local outputs, not
new tracked truth. An untrusted reporting executable could lie: use a separately reviewed checkout.

## Separate design QA

The pre-implementation QA made these decisions explicit:

- Putting an owner bypass in the Gate ruleset would permit skipping verification. The update-only
  rule is separate, uses `pull_request`, and names exactly the owner; wider or hidden bypass
  configurations are refused. No Integration, DeployKey, broad role, or `always` fallback is used.
- Required self-approval is impossible for an owner-authored PR. The owner's deliberate merge is
  authorization, not an independent approving review. Same-account agent credentials are the same
  GitHub identity and cannot be distinguished by YAML, signatures, or a report.
- Pending approval of a workflow run, green CI, a draft flag, and human authorization are different
  states. Reports keep them distinct. A complete report cannot be produced from a truncated tree,
  inconsistent paginated CI response, stale merge result, or duplicate job/run identity.
- A classifier is review guidance, not an authority grant. All changed paths remain in the report;
  reporting/gate/registry edits receive contract-policy review even if renamed or newly introduced.
  The policy's own source is included in the reporting-tool identity.
- Working-tree status alone can hide assume-unchanged changes. Tool provenance checks actual file
  bytes and modes against the committed tree, rejects symlinks and untracked tooling, and does not
  treat a claimed commit string as proof of the executable being used.
- Settings application is not transactional. It remains opt-in, plan-hash-bound, read-before-write,
  read-after-write, non-retrying and non-rollback. Unsupported existing restrictions need owner
  reconciliation; changes in repository or owner identity abort. This PR does not activate settings.

## Owner procedure

From a clean, reviewed checkout of this tooling, using an existing read credential in `GH_TOKEN`:

```text
node eng/ci/owner-review.mjs --repository resoltico/claimcore --pr <number> --head <full-head-sha>
```

The JSON contains a digest binding the report, the exact revisions, all changed paths and review
scopes, CI status, and `ownerAuthorization: not-granted-by-this-report`. Exit zero means a complete
report with successful current-head CI on a non-draft open PR, **not approval or merge permission**.
Exit two means a valid report with pending/failed CI or a draft. Exit one means the report could not
be established. No remote write occurs on any path. Run again after any head/base/attempt change.

Review the entire diff between the reported base and tested merge. For architecture, verify actual
ownership and dependency edges, not just the new manifest. For security, review credentials,
admission, privacy, durable authority and supply-chain impact. For contract-policy, review meaning,
encoding, expected results, assertions, coverage/exclusions, scenario membership, gate reachability,
and changes to the reporter itself. General changes still require ordinary owner review. The
report is an inventory and checklist, not a substitute for understanding the diff.

Immediately before your manual GitHub merge, recheck the reported head/base, ready status, current
checks and resolved conversations. Keep the merge event as GitHub's actor/time/commit record. Do not
have the implementing agent merge, approve, enable auto-merge or claim that source review is your
authorization. A new commit invalidates the prior report. With concurrent activity, stop and review
the new revisions instead of treating an earlier intent as approval of unseen work.

## Activation and credential boundary

Review the existing settings plan/apply/check procedure in [CI governance](ci-governance.md).
The plan now includes the update-only owner rule and disabling auto-merge. Applying it requires a
separate owner-authenticated administration step. No source merge can activate a ruleset by itself.
A missing or inaccessible bypass list is unknown policy, not an empty list. Pre-existing wider,
inherited, or incompatible owner restrictions stop the plan instead of being silently rewritten.

After activation verify the actual rulesets and credentials: a content-writing collaborator/app can
publish a feature branch and PR but cannot merge it; the owner cannot directly update `main` or
merge failed CI; the owner can merge their own passing PR without self-approval. Verify these in an
isolated test repository before relying on deployment; mocked tests are not live authorization
qualification. Keep ordinary automation without administration access or an owner identity. Never
export an owner credential to Actions or the implementation agent.

If the agent uses the owner's user token, GitHub sees the owner. The update restriction does not
protect against that credential holder. Strong human/agent separation requires a distinct
non-owner automation identity and owner-retained credentials; changing the integration credentials
is outside this PR. Administrators can deliberately change protections: no repository-local policy
claims to defeat its own administrator.

## Platform references

GitHub documents [repository rules](https://docs.github.com/en/rest/repos/rules),
[combined ruleset enforcement](https://docs.github.com/en/repositories/configuring-branches-and-merges-in-your-repository/managing-rulesets/about-rulesets),
[personal repository roles](https://docs.github.com/en/repositories/managing-your-repositorys-settings-and-features/repository-access-and-collaboration/permission-levels-for-a-personal-account-repository),
and [review restrictions](https://docs.github.com/en/pull-requests/how-tos/review-pull-requests/approving-a-pull-request-with-required-reviews).
The User bypass and PR-only mode follow the current API contract; an unsupported response is a
failure requiring owner review, not permission to fall back to a broader actor.
