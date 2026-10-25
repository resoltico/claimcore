# Browser development

Start here to build or change the React browser. It uses the same authenticated HTTPS service as CLI, with
no database access. [Browser and host](../docs/web.md) explains review, consent and outcome resolution;
[Service and CLI](../docs/api.md) explains byte admission and delivery. Domain/Application decide the business behavior.

## Find the owning module

- [api/transport.ts](src/api/transport.ts) owns networking; [api/service.ts](src/api/service.ts) is the validated service facade.
- [protocol/schema.ts](src/protocol/schema.ts) admits bounded strict JSON before the generated executable request/response checks. Protocol modules remain independent from views and presentation.
- [interaction/operationForm.ts](src/interaction/operationForm.ts) assembles exact member paths, tagged branches and diagnostic focus from typed interaction bindings; [hooks/operation/useOperation.ts](src/hooks/operation/useOperation.ts) retains the exact prepared request, consent and prior uncertainty for CASE and AUTHORITY.
- [views](src/views/) render context-authorized navigation, cases/history, operation review/observation and authority work. Domain/Application own validation, available commands and outcome meaning.
- [presentation](src/presentation/) renders authored [source catalogs](../src/ClaimCore.Contract/Localization/) in English, Latvian and Arabic. Language and display formats never rewrite business values. Latvian and Arabic text needs native review. Language/display preferences persist in browser storage. While unresolved, sessionStorage holds only the operation ID, scope and act; reload offers Check outcome without retained request bytes.

## Build locally

Use the Node/npm versions in [.node-version](../.node-version) and [package.json](package.json), with [package-lock.json](package-lock.json) for dependencies. From the repository root:

```sh
npm --prefix web ci
npm --prefix web run build
```

`build` typechecks, produces `dist` and generates notices from the locked production graph. Commands generate ignored localization typing metadata from the source catalogs; pseudolocalization runs at runtime. The F# generator owns the three tracked [contracts](../contracts/); web commands consume them without invoking .NET. Ordinary .NET builds do not run npm.

[Development](../docs/development.md#verification-stages) owns the frontend stage and real-browser qualification, including complete three-engine PR qualification, isolated engine jobs and serial workers within each stateful fixture. Formatting, type-aware lint, module boundaries, unit completion, audit, licenses, Knip and notices are checked. Coverage is optional; CSS is checked through formatting and browser layout/accessibility, without a separate semantic linter.
