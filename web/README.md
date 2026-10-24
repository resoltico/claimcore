# ClaimCore browser

React and react-aria-components render one generated service contract. The browser is a same-origin authenticated HTTPS client with no database access. [Browser and host](../docs/web.md) owns runtime behavior, consent, disclosure and retry rules; [API](../docs/api.md) owns wire admission and delivery semantics.

## Source map

- [api/transport.ts](src/api/transport.ts) owns networking; [api/service.ts](src/api/service.ts) is the validated service facade.
- [protocol/schema.ts](src/protocol/schema.ts) admits bounded strict JSON against the generated OpenAPI schemas. Protocol modules remain independent from views and presentation.
- [interaction/operationForm.ts](src/interaction/operationForm.ts) derives request fields from schemas and structural descriptors; [hooks/operation/useOperation.ts](src/hooks/operation/useOperation.ts) retains the exact prepared request, consent and prior uncertainty for CASE and AUTHORITY.
- [views](src/views/) render context-authorized navigation, cases/history, operation review/observation and authority work. Domain/Application own validation, available commands and outcome meaning.
- [presentation](src/presentation/) renders authored [source catalogs](../src/ClaimCore.Contract/Localization/) in English, Latvian and Arabic. Language and display formats never rewrite business values. Latvian and Arabic text needs native review. Only language/display preferences persist in browser storage; requests and claimant state do not.

## Build and checks

Use the Node/npm versions in [.node-version](../.node-version) and [package.json](package.json), with [package-lock.json](package-lock.json) for dependencies. From the repository root:

```sh
npm --prefix web ci
npm --prefix web run build
```

`build` typechecks, produces `dist` and generates notices from the locked production graph. Commands generate ignored localization typing metadata from the source catalogs; pseudolocalization runs at runtime. The F# generator owns the three tracked [contracts](../contracts/); web commands consume them without invoking .NET. Ordinary .NET builds do not run npm.

[Development](../docs/development.md#verification-stages) owns the frontend stage and real-browser qualification, including the partial PR smoke scope, isolated engine jobs and serial workers within each stateful fixture. Formatting, type-aware lint, module boundaries, unit completion, audit, licenses, Knip and notices are checked. Coverage is optional; CSS is checked through formatting and browser layout/accessibility, without a separate semantic linter.
