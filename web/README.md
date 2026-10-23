# ClaimCore browser

React and react-aria-components render the generated service contract. Domain and Application
own validation, available commands and outcome meaning. The browser is an authenticated
same-origin HTTPS client with a contract identity header and cookie antiforgery token.

`src/api/transport.ts` owns browser networking; `src/api/service.ts` is its validated service facade.
A single `src/protocol/schema.ts` interpreter admits bounded strict JSON replies against generated
OpenAPI component schemas. Requests use one schema-driven form engine with core field order, prefill and advice.
`src/interaction` and operation hooks preserve authored values, review identity and the exact
serialized request. Preview is advisory. Confirmation executes that request; an unknown
outcome or lost execution reply keeps one exact retry action while the page lives. A contract
mismatch offers explicit reload. No request or claimant state is persisted in browser storage.
Only language and display preferences are persisted.

Views show case lists, live/voided/erased cases, accepted history, command review and operation
observation. An erased case retains its reference and revision. Stewardship and authority
management use typed core advice and descriptor-driven forms in the browser. Display language, numeric/date formatting and Unicode
warnings never rewrite authored or accepted values.

## Build and checks

Node/npm versions are pinned by `.node-version` and `package.json`; dependencies by the npm lock.
From the repository root, run `npm --prefix web ci`, then `npm --prefix web run build`.
`bash eng/verify.sh frontend` runs formatting, lint, types, unit tests, Knip, audit, licenses,
build and notices using Node only. See [Development](../docs/development.md) for named stages.

The complete generated consumer bundle is tracked in `contracts/`. The F# generator owns their bytes;
.NET freshness tests check exact inventory and content. No web command invokes .NET. Generated
`client.ts` types, OpenAPI component schemas and the strict-byte profile govern requests and
responses. Direct correlation and frozen-request uncertainty rules have independent examples and real-service checks. Views
come from `context.views`; OPEN and case actions come from typed advice rather than endpoint names.

English, Latvian and Arabic metadata and diagnostics come from the same authored source JSON
catalogs, with matching keys and template arguments. Latvian and Arabic text is machine-drafted and needs native review.
Every direct consumer generates ignored localizations first. Pseudolocalization runs at runtime;
there is no tracked pseudolocale or compiled catalogue. Display language and formatting never
rewrite authored business values or country codes.

`build` typechecks, produces `dist` and generates independent notices from the locked production
graph. Ordinary .NET builds do not run npm. Publishing consumes assets; the Node container stage
builds them before SDK publication. Source formatting and type-aware lint retain size/complexity
limits and networking/localization negative controls. Coverage is optional and gates nothing.
CSS verification covers formatting and browser layout/accessibility; there is no separate CSS
semantic linter.

Serialization and real browser acceptance cover correction combinations, localized validation,
consent and exact retries. Each fixture has one worker and zero retries because grants/relay
state are shared. CI isolates engines across jobs; complete qualification runs all specifications
on Chromium, Firefox and WebKit. See [Development](../docs/development.md) for local sequencing
and the explicitly partial PR smoke selection.

Authored localization lives in `src/ClaimCore.Contract/Localization` at the repository root.
The browser imports those catalogs directly; ignored local outputs contain derived typing metadata.
`useOperation` and its reducer retain the same prepared request and prior uncertainty for both scopes.
