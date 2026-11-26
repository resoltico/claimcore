# Browser development

See the [web UI guide](../docs/web-ui.md) for building blocks and CSS ownership.

The React browser uses the authenticated HTTPS service, with no database access. See
[Integrating](../docs/integrating.md) for wire contracts and
[Operating](../docs/operating.md#identity-and-hosting) for host configuration.

## Change the browser

- [api/transport.ts](src/api/transport.ts) owns networking; [api/service.ts](src/api/service.ts) parses server replies and checks representation and operation-ID echoes.
- [protocol](src/protocol/) keeps JSON parsing, byte bounds and typed DTO handling independent of React, DOM and Node.
- [taskForm](src/operation/taskForm.ts) assembles typed commands from semantic task inputs and reviewed bindings.
- [operationState](src/operation/operationState.ts) governs drafts, review consent, disclosed reads and delivery. [useOperation](src/operation/useOperation.ts) wires those transitions to browser effects.
- [ScreenView](src/ui/ScreenView.tsx) and [region components](src/ui/regions/) render the server's locale-neutral view; [PresentedValue](src/components/PresentedValue.tsx) formats semantic values.
- [Features](src/features/) own interactions and the [shell](src/shell/) composes routes. Domain/Application own eligibility, transitions, disclosure and timing.

[Presentation](src/presentation/) fetches the build's resolved Core and chrome catalogs from
immutable same-origin asset paths. Preparation applies the shared language policy, English-source
hashes and fallback. The build validates the prepared catalog sources and message shapes. The browser
admits a complete pair before activating its language and direction. Regional formatting remains
independent, and preference changes do not reinterpret entered amounts. The CLI loads its own
catalogs with the shared language policy.

[Recovery](src/recovery/) stores operation and attempt identities locally under the origin,
installation and account scope before dispatch. The journal excludes business payloads and credentials.
A visible tab owns automatic checks while its lease is valid. When coordination is unavailable,
the workspace offers a manual check for the selected operation. Credentials and session ownership
fence responses, and a restart check can recover the outcome without reconstructing the request.

## Build and verify

Use [.node-version](../.node-version), [package.json](package.json) and its lockfile. From the repository root:

```sh
npm --prefix web ci
npm --prefix web run prepare:catalogs
npm --prefix web run build
npm --prefix web run test:unit -- tests/unit/ui/ScreenRegions.test.tsx
```

Catalog preparation uses the pinned .NET SDK and locked restore. Prepare again after changing
authored catalogs or language policy. The Web build consumes the prepared bytes and tracked contracts,
checks the app's protocol imports, and emits `dist` with the public asset inventory and notices.
Full typecheck also covers unit and end-to-end inputs. Use the
[generator](../docs/contributing.md#generated-contracts) after changing messages or contracts.

Units live under tests/unit, interactions under tests/flows, and fixtures under tests/support. Use
file or name selectors for focused work. Required checks verify every selected test result. The
[verification guide](../docs/contributing.md#verification) defines focused checks and final scope.
Chromium runs all journeys; Firefox/WebKit run the @engine risk subset. Retain strict CSP, keyboard,
RTL, forced colours and exact-value behavior. Read admission belongs to the server; obsolete reads cancel.
