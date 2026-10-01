# ClaimCore Web frontend

This directory contains the React/TypeScript client served by `ClaimCore.Web`. It is a renderer for
the generated Web-v3 contract; business validation, available commands, operation identity, and
failure meaning remain in the F# Domain and Application projects.

## Source layout

- `src/api/` owns same-origin HTTP transport and response validation.
- `src/components/` contains reusable accessible controls and review surfaces.
- `src/hooks/` coordinates session, query, operation, and recovery state.
- `src/views/` contains the login, case, command, and recovery workflows.
- `src/generated/` contains checked projections from the F# HTTP contract and must not be hand-edited.
- `tests/` contains Vitest component and behavior tests; `e2e/` contains the published-host Playwright
  lifecycle.
- `scripts/` contains bounded contract, manifest, notice, SBOM, and test-report helpers.

## Toolchain and commands

The exact Node and npm versions are declared by [`.node-version`](../.node-version) and
[`package.json`](package.json). The package lock is authoritative for the installed graph.

The `tsc` command is the native TypeScript 7 compiler and the only TypeScript installed; it types and
builds ClaimCore. Linting is oxlint with type-aware rules on the same native toolchain
(`oxlint-tsgolint`), run by `scripts/lint.mjs` once per TypeScript project (`scripts/lint-projects.mjs`)
because type-aware rules read one program at a time. `oxc-parser` reads the generated recovery types
for the localization vocabulary, since TypeScript 7 ships no JavaScript compiler API. Boundary rules
that no built-in rule expresses live in `lint/claimcore-plugin.mjs`. Every lint, type, format and
coverage exception is registered in [`config/lint-exceptions.json`](../config/lint-exceptions.json).
Composite project references retain incremental
state in ignored files, and Vitest uses isolated, machine-scaled file workers. The locked StrykerJS
gate (its sandbox tsconfig rewrite is disabled because it needs a compiler API TypeScript 7 does not ship; Vitest needs no tsconfig) mutates only `src/domain/operationReducer.ts` and checks a 92% minimum against exact-source
local evidence; it is not F# or PostgreSQL mutation coverage.

[`package.json`](package.json) defines the individual npm scripts. Their canonical ordered use,
including formatting, typechecking, linting, dependency assurance, tests, and asset production, is in
[Development](../docs/development.md#frontend-assurance). Do not create a second frontend build path:
`npm run build` is the sole asset producer.

## Generated contract and publish boundary

`npm run contract:generate` regenerates semantic, CLI-v4, and Web-v3 catalogs, exact endpoint response
schemas, pure-codec corpora, and split TypeScript DTO modules from F#. A deterministic Node
postprocess compiles the aggregate Web response graph into typed AJV standalone core and recovery
validator groups, formats generated TypeScript, and binds the complete inventory in one manifest. The
browser dynamically imports only the group required to validate a received endpoint response.
The output lives in the ignored `src/generated/contracts/` and must match the committed
`config/contracts.lock.json` (length and SHA-256 per artifact). `contract:verify` and `contract:check`
verify an existing directory without regenerating. Use `contract:lock` only for an intentional contract
change, then review the lock diff with its production codec and tests.

The Vite output under `dist/` is ignored. Asset production records a manifest bound to frontend
source, the npm lock, generated contract, Node/npm versions, notices, and output bytes. The .NET Web
publish target consumes only a matching manifest; an ordinary `dotnet build` neither runs npm nor
silently accepts stale assets.

Browser operation and private runtime configuration belong in the [Web
reference](../docs/web.md). The browser suite always targets published bytes and independently
freshly initialized synthetic databases, never a Vite development server.
