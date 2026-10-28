# Browser development

Start here to build or change the React browser. It uses the same authenticated HTTPS service as CLI, with
no database access. [Browser and host](../docs/operating.md#identity-and-hosting) explains hosting;
[Service and CLI](../docs/integrating.md) explains byte admission and delivery. Domain/Application decide the business behavior.

## Find the owning module

- [api/transport.ts](src/api/transport.ts) owns networking; [api/service.ts](src/api/service.ts) is the validated service facade.
- [protocol/schema.ts](src/protocol/schema.ts) admits bounded strict JSON before the generated executable request/response checks. Protocol modules remain independent from views and presentation.
- [operation/operationForm.ts](src/operation/operationForm.ts) assembles exact member paths, tagged branches and diagnostic focus from typed catalog bindings; [hooks/operation/useOperation.ts](src/hooks/operation/useOperation.ts) retains the exact prepared request, consent and prior uncertainty for CASE and AUTHORITY.
- [views](src/views/) render context-authorized navigation, cases/history, operation review/observation and authority work. Domain/Application own validation, available acts and outcome meaning.
- [presentation](src/presentation/) combines generated messages from the shared [core catalogs](../src/ClaimCore.Protocol/Messages/Localization/) and browser-owned [chrome catalogs](locales/) in English, Latvian and Arabic. Language and display formats never rewrite business values. Latvian and Arabic text needs native review. Language/display preferences persist in browser storage. The [integration guide](../docs/integrating.md#delivery-recovery) owns interrupted-delivery behavior.

[Operation state](src/operation/operationState.ts) uses the models in
[operationTypes.ts](src/operation/operationTypes.ts). [inputErrors.ts](src/operation/inputErrors.ts) maps
ordered server issues to field notices; each field renders every associated issue and the form focuses the
first invalid input. Diagnostic message keys come from the generated registry.

Unit tests mirror source modules under `tests/unit`, integrated scenarios are under `tests/flows`, and
fixtures/setup live under `tests/support`. Lint rejects orphan unit paths. Typed `p.term` calls select the
core vocabulary explicitly; generated scalar guidance and catalog country metadata supply rule meaning.

## Build locally

Use the Node/npm versions in [.node-version](../.node-version) and [package.json](package.json), with [package-lock.json](package-lock.json) for dependencies. From the repository root:

```sh
npm --prefix web ci
npm --prefix web run build
```

`build` typechecks app sources and their strict protocol dependency, produces `dist` and generates notices
from the locked production graph. The full `typecheck` command includes tests and e2e sources. Production
builds need no test fixtures.
Review summaries use catalog bindings and JSON values, preserving unknown members as raw JSON; OpenAPI is
not bundled. Pseudolocalization runs at runtime. The [F# generator](../eng/ClaimCore.ProtocolGenerator/Program.fs) owns the tracked [contracts](../contracts/), [CLI reference](../docs/cli.md) and [browser chrome messages](src/generated/chrome-messages.ts); web commands consume generated outputs without invoking .NET. Ordinary .NET builds do not run npm.

[Development](../docs/contributing.md#verification) owns the frontend stage and real-browser qualification, including complete three-engine PR qualification, isolated engine jobs and serial workers within each stateful fixture. Formatting, type-aware lint, module boundaries, unit completion, audit, licenses, Knip and notices are checked. CSS is checked through formatting and browser layout/accessibility, without a separate semantic linter.

See the [glossary](../docs/glossary.md) for project terminology.
