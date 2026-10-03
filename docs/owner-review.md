# Owner review and merge authorization

This document owns the sole-owner authorization boundary. [CI governance](ci-governance.md) owns
workflow integrity and the settings procedure; [Development](development.md) owns verification.
The policy is for a personal repository with default branch `main`. Organization ownership or a
merge queue requires a separately reviewed policy, not an automatic extension of this one.

## Three independent facts

CI tested a particular source, an agent reviewed it, and the repository owner decided to merge particular
revisions. Only the last is authorization. No CI result, source review, PR author checkbox, comment command,
label or agent declaration establishes it, and GitHub's merge event (actor, time, commit) is the record of it.

## Native rules on `main`

The required boundary uses two rulesets; the settings procedure in [CI governance](ci-governance.md) plans, applies
and reads them back. These are required settings, not a statement that a source checkout has
activated them; inspect native state with the read-only settings check before relying on enforcement.

- The verification ruleset requires the strict Actions `Gate`, a PR, resolved review threads, no deletion and no
  non-fast-forward update, with **no bypass actors**.
- A separate update-restriction ruleset permits only the repository owner's numeric **User** identity to bypass the
  update restriction, and only through a PR. It contains no status-check exception. GitHub combines applicable
  rulesets: the owner still must satisfy the separate verification rules.

New PR rules require zero approving reviews (required self-approval is impossible for an owner-authored PR); stronger
pre-existing review rules are not weakened. Repository auto-merge is disabled so a deferred merge cannot stand in for
a decision on the current head. No merge bot or privileged PR workflow exists.

## Owner procedure

Review the entire diff between the PR's base and its tested merge. GitHub shows the merge tree and `gh pr checks`
shows the `Gate` for the current head; read both. For architecture, verify actual ownership and dependency edges, not
just the new manifest. For security, review credentials, admission, privacy, durable authority and supply-chain
impact. For contract changes, review the meaning, encoding and expected results together with
[`config/contracts.lock.json`](../config/contracts.lock.json) and the tests that name the contract. For test changes,
read the diff of `tests/inventory/`; it changes whenever a test is added, renamed or removed. General changes still
require ordinary owner review.

Immediately before the manual GitHub merge, recheck the head and base, ready status, current checks and resolved
conversations. Do not have the implementing agent merge, approve, enable auto-merge or claim that source review is
the owner's authorization. A new commit invalidates the prior review. With concurrent activity, stop and review the
new revisions instead of treating an earlier intent as approval of unseen work.

## Activation and credential boundary

Applying the settings requires a separate owner-authenticated administration step; no source merge can activate a
ruleset by itself. A missing or inaccessible bypass list is unknown policy, not an empty list. Pre-existing wider,
inherited, or incompatible owner restrictions stop the plan instead of being silently rewritten.

After activation verify the actual rulesets and credentials: a content-writing collaborator or app can publish a
feature branch and PR but cannot merge it; the owner cannot directly update `main` or merge failed CI; the owner can
merge their own passing PR without self-approval. Verify these in an isolated test repository before relying on
deployment; mocked tests are not live authorization qualification. Keep ordinary automation without administration
access or an owner identity. Never export an owner credential to Actions or the implementation agent.

If the agent uses the owner's user token, GitHub sees the owner. The update restriction does not protect against that
credential holder. Strong human/agent separation requires a distinct non-owner automation identity and
owner-retained credentials. Administrators can deliberately change protections: no repository-local policy claims to
defeat its own administrator.

## Platform references

GitHub documents [repository rules](https://docs.github.com/en/rest/repos/rules),
[combined ruleset enforcement](https://docs.github.com/en/repositories/configuring-branches-and-merges-in-your-repository/managing-rulesets/about-rulesets),
[personal repository roles](https://docs.github.com/en/repositories/managing-your-repositorys-settings-and-features/repository-access-and-collaboration/permission-levels-for-a-personal-account-repository),
and [review restrictions](https://docs.github.com/en/pull-requests/how-tos/review-pull-requests/approving-a-pull-request-with-required-reviews).
The User bypass and PR-only mode follow the current API contract; an unsupported response is a
failure requiring owner review, not permission to fall back to a broader actor.
