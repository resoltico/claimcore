# Browser development

The React browser uses the authenticated HTTPS service, with no database access. See
[Integrating](../docs/integrating.md) for wire contracts and
[Operating](../docs/operating.md#identity-and-hosting) for host configuration.

## Change the browser

- [api/transport.ts](src/api/transport.ts) owns networking; [api/service.ts](src/api/service.ts) admits replies and checks typed operation-ID echoes.
- [protocol](src/protocol/) keeps strict JSON/DTO handling independent of React, DOM and Node.
- [operationForm](src/operation/operationForm.ts) assembles requests from catalog paths, branches and constants.
- [useOperation](src/hooks/operation/useOperation.ts) owns prepared bytes, consent and delivery uncertainty.
- [ScreenView](src/ui/ScreenView.tsx) and [TableView](src/ui/TableView.tsx) render the server's locale-neutral view; [PresentedValue](src/components/PresentedValue.tsx) formats semantic values.
- [views](src/views/) own navigation and interactions. Domain/Application own eligibility, transitions, disclosure and timing.

[Presentation](src/presentation/) fetches resolved core and chrome text from the same-origin public
`/catalogs/{language}.json` endpoint. Presentation in the host applies English-source hashes and
fallback; the browser admits the complete pair before activating it. Chrome keys come from authored
English JSON. The CLI uses the same native resolver. A complete pair activates with its direction; regional formatting remains independent, and preference changes do not reinterpret entered amounts.

[Recovery](src/recovery/) persists only origin, installation, account and local attempt identity before
handoff. It never persists business payloads or credentials. Async callbacks retain credential/session
ownership; a replacement identity invalidates obsolete results. Restart checks cannot recreate a request.

## Build and verify

Use [.node-version](../.node-version), [package.json](package.json) and its lockfile. From the repository root:

```sh
npm --prefix web ci
npm --prefix web run build
npm --prefix web run test:unit -- tests/unit/ui/RecordView.test.tsx
```

The app build checks its strict protocol dependency, emits `dist` and generates notices. Full typecheck
also covers unit/e2e inputs. Web commands consume the tracked contracts and authored chrome catalogs and do not run .NET.
Use the [generator](../docs/contributing.md#generated-contracts) after changing messages or contracts.

Units live under tests/unit, interactions under tests/flows, and fixtures under tests/support. Run full
selected files; name filtering can produce forbidden skipped tests. The
[verification guide](../docs/contributing.md#verification) defines focused checks and final scope.
Chromium runs all journeys; Firefox/WebKit run the @engine risk subset. Retain strict CSP, keyboard,
RTL, forced colours and exact-value behavior. Read admission belongs to the server; obsolete reads cancel.
