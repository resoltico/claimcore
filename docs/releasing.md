# Releasing ClaimCore

ClaimCore publishes source previews. The publisher validates the dated
`## [X.Y.Z] - YYYY-MM-DD` section in [`CHANGELOG.md`](../CHANGELOG.md), then publishes its exact
contents beneath that heading. It omits the changelog heading and separator because GitHub already
displays the release title and publication time. The publisher does not summarize, paraphrase, append
verification links, add legal prose, generate notes, or upload application assets. GitHub supplies
source archives for the annotated tag automatically.

Historical releases were manually authored before this policy existed. The publisher deliberately
refuses to rewrite an existing release whose title, body, flags, or assets do not match its current
changelog-derived plan; correct a historical release only through a separate, reviewed operation.

## Before publication

1. Complete the release PR: set the sole product version in `Directory.Build.props`, move the reviewed
   customer-facing entries from `Unreleased` into the dated changelog section, and leave an empty
   `Unreleased` section.
   When the numeric version changes in an existing checkout, clean both configurations before
   rebuilding: `dotnet clean ClaimCore.slnx --configuration Release` and
   `dotnet clean ClaimCore.slnx --configuration Debug`. Retained F# reference assemblies can otherwise
   keep the old assembly identity even when implementation assemblies have the new version.
2. Merge the PR and require the exact merge commit's successful main `Gate`.
3. Dispatch `verify-properties.yml` on that exact main candidate with an explicit canonical base seed;
   require its complete extended unit/fuzz profile to succeed and confirm the run's `headSha`.
   A scheduled result on another commit is not release evidence. For example:
   `gh workflow run verify-properties.yml --ref main -f base_seed=20261003`.
4. Create and push a new annotated `vX.Y.Z` tag for that exact commit. Never move or overwrite a tag.
5. Require the tag-push `Gate` for that exact tag and commit to pass.

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

## Licensing and source distribution

The repository [LICENSE](../LICENSE) carries the current project-wide notice and governing text.
Earlier revisions keep the terms in their own license files; the [changelog](../CHANGELOG.md)
records release transitions. Do not rewrite published tags, license files, archives or release text.
Third-party components retain their own licenses and notices.

Public releases currently distribute source previews through the matching immutable tag archives.
Before distributing application binaries or browser HTML, CSS and JavaScript outside your
organization, provide recipients
the actual corresponding MPL-covered source, build inputs and modifications, and a clear way to
obtain it without charging more than distribution cost. For an unchanged official release, use
its matching tagged source archive. A development version, dirty checkout or modified build
requires its corresponding source; linking an older release or repository HEAD is insufficient.

Qualified binary outputs carry LICENSE; browser assets carry LICENSE.txt and an HTML license/source
notice. .NET publication preserves each restored package's declared copyright, license files,
NOTICE and third-party notices; a top-level SPDX identifier does not replace embedded notices.
Packages without license files use reviewed permission text alongside their own attribution.
Preserve those notices and third-party attribution. When redistributing a modified build,
update the source-availability notice to identify the source you actually provide. Do not put
private configuration, credentials, data or generated test evidence in a source distribution.
See [Mozilla's distribution guidance](https://www.mozilla.org/en-US/MPL/2.0/FAQ/) and
[MPL sections 3.1–3.4](https://www.mozilla.org/en-US/MPL/2.0/) for the requirements.
