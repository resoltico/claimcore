# Releasing ClaimCore

ClaimCore publishes source previews. Each public GitHub Release body is the exact dated section from
[`CHANGELOG.md`](../CHANGELOG.md), including its `## [X.Y.Z] - YYYY-MM-DD` heading. The publisher
does not summarize, paraphrase, append verification links, add legal prose, generate notes, or upload
application assets. GitHub supplies source archives for the annotated tag automatically.

Historical releases were manually authored before this policy existed. The publisher deliberately
refuses to rewrite an existing release whose title, body, flags, or assets do not match its current
changelog-derived plan; correct a historical release only through a separate, reviewed operation.

## Before publication

1. Complete the release PR: set the sole product version in `Directory.Build.props`, move the reviewed
   customer-facing entries from `Unreleased` into the dated changelog section, and leave an empty
   `Unreleased` section.
2. Merge the PR and require the exact merge commit's successful main `Gate`.
3. Create and push a new annotated `vX.Y.Z` tag for that exact commit. Never move or overwrite a tag.
4. Require the tag-push `Gate` for that exact tag and commit to pass.

The tag must identify an ancestor of current `main`. The publisher reads `Directory.Build.props` and
`CHANGELOG.md` by that immutable tag commit, not from the workflow checkout or later `main` state.

## Publish

The GitHub `release` environment must exist, be restricted to the `main` branch, and have any desired
reviewers configured before first use. The workflow refuses non-main dispatches and serializes
publication attempts.

```sh
gh workflow run release.yml \
  --repo resoltico/claimcore \
  --ref main \
  -f tag=vX.Y.Z \
  -f expected_sha="$(git rev-parse 'vX.Y.Z^{commit}')"
```

The workflow validates the annotated tag, version, main ancestry, newest tag-push CI run, and that
run's current-attempt aggregate `Gate` before it creates a private draft. It rereads the draft and
verification state, publishes only by clearing the draft flag, then rereads the public release. A
matching published release is a no-op; a matching draft can resume. A mismatch, changed tag, CI rerun,
or uncertain request outcome stops the workflow without rewriting public text or attempting rollback.

Run the offline policy tests with:

```sh
node --test eng/release/*.test.mjs
```
