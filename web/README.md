# ClaimCore browser

React and react-aria-components render the generated service contract. Domain and Application
own validation, available commands and outcome meaning. The browser is an authenticated
same-origin HTTPS client with a contract fingerprint header and cookie antiforgery token.

`src/api/service.ts` owns all browser networking and validates bounded strict JSON replies.
`src/domain` and operation hooks preserve authored values, review identity and the exact
serialized request. Preview is advisory. Confirmation executes that request; an unknown
outcome or lost execution reply keeps one exact retry action while the page lives. A contract
mismatch offers explicit reload. No request or claimant state is persisted in browser storage.
Only language and display preferences are persisted.

Views show case lists, live/voided/erased cases, accepted history, command review and operation
observation. An erased case retains its reference and revision. Stewardship and authority
management remain API/CLI operations. Display language, numeric/date formatting and Unicode
warnings never rewrite authored or accepted values.

## Build and checks

Node/npm versions are pinned by `.node-version` and `package.json`; dependencies by the npm lock.
From the repository root:

```sh
npm --prefix web ci
npm --prefix web run contract:generate
npm --prefix web run localization:check
npm --prefix web run typecheck
npm --prefix web run lint
npm --prefix web run lint:styles
npm --prefix web run test:unit
npm --prefix web audit --audit-level=low
npm --prefix web run build
```

`format:check` checks authored source. Function size and complexity remain enforced. Coverage
is available through `test:coverage` and gates nothing. Networking and localization boundaries
have positive and rejecting policy fixtures.

Contract generation runs the F# generator, compiles strict standalone AJV validators, then
checks every artifact's length/hash against `config/contracts.lock.json`. Generated files live
in ignored `src/generated/contracts`; never edit them. `contract:lock` accepts an intentional
change only after reviewing it with the owning F# change. A failed or interrupted generation
must be rerun before consumers can pass lock verification.

English business metadata and diagnostics come from F#. The authored Latvian and Arabic
catalogs must cover the same keys, parameter roles and rendered tokens. `localization:generate`
derives all compiled catalogs and the English pseudolocale. New translations in this redesign
are machine-drafted and need native review.

`build` typechecks, produces `dist` with Vite and includes third-party notices for runtime
packages and embedded validators. Ordinary .NET builds do not run npm. Publishing consumes
built assets; the container build and published acceptance own the delivered source/build
relationship. The three-engine Playwright suite targets a freshly started published service,
not a development server. See `docs/development.md` for repository-wide verification.

The Stylelint tooling graph pins the MIT-licensed `@dieub/braces-depth-guard@3.0.3-pn.2`
derivative through a `braces` override. Its published runtime diff and nesting rejection were
reviewed to address GHSA-vfj7-8cjw-p6xm, for which upstream has no patched release. This is a
code replacement, not an audit exclusion; the audit remains at `low`. The dependency review
and compatibility/rejection evidence are in the decision record.
