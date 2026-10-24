# Synthetic case walkthrough

Run against a fresh synthetic installation with matching published service and CLI binaries.
The CLI is an authenticated HTTPS client using the [OpenAPI contract](../contracts/openapi.json).
It receives no database connection file. The Web service requires a persisted protected key-ring
directory; use the [fresh setup](../docs/getting-started.md) to create it.

Configure `CLAIMCORE_SERVICE_URL`, `CLAIMCORE_OIDC_ISSUER`, `CLAIMCORE_OIDC_CLIENT_ID`
and `CLAIMCORE_CLI_AUTH_MODE` as described by `ClaimCore.Cli help`. For automation, supply
its private client-secret file. For local HTTPS certificates outside system trust, configure
the explicit CLI service and OIDC trust-root files. The selected actor needs installation `CASE_HANDLER` and `PAYMENT_RECORDER` grants.
An installation `OWNER` grants authority; that role alone does not grant case work.

From the repository root, execute the six business frames, in order:

```sh
dotnet run --project src/ClaimCore.Cli --configuration Release --no-build -- call < examples/01-open.json
dotnet run --project src/ClaimCore.Cli --configuration Release --no-build -- call < examples/02-decide.json
dotnet run --project src/ClaimCore.Cli --configuration Release --no-build -- call < examples/03-record-payment.json
dotnet run --project src/ClaimCore.Cli --configuration Release --no-build -- call < examples/04-close.json
dotnet run --project src/ClaimCore.Cli --configuration Release --no-build -- call < examples/05-reopen.json
dotnet run --project src/ClaimCore.Cli --configuration Release --no-build -- call < examples/06-correct-payment-record.json
```

The human verbs use the same typed inputs. For example, after step three, this prints the
same request frame as `04-close.json` without authentication or a network call:

```sh
dotnet run --project src/ClaimCore.Cli --configuration Release --no-build -- case close DEMO-0001 --expected-revision 3 --operation-id 10000000-0000-4000-8000-000000000004 --dry-run
```

Use `help verbs` for the registry-derived grammar; omit `--dry-run` to execute, or send the
printed frame unchanged to `call`. Verbs default to localized text; `--format json` returns a
structured reply. The numbered JSON files remain independent, exact request examples.

Each returns exit 0 and `ACCEPTED`. `DEMO-0001` ends at revision 6, open, with its
payment date cleared. Payment facts are recorded assertions; no money is transferred.
While its payload is retained and current authority permits replay, an exact retry returns
`ALREADY_ACCEPTED` with the original acceptance metadata and currently authorized snapshot
disclosure. A preview is advisory; execution validates the locked current state.

The remaining three frames require a human `DATA_STEWARD` grant. They void the case
for an entry error, reinstate it and erase it:

```sh
dotnet run --project src/ClaimCore.Cli --configuration Release --no-build -- call < examples/07-void.json
dotnet run --project src/ClaimCore.Cli --configuration Release --no-build -- call < examples/08-reinstate.json
dotnet run --project src/ClaimCore.Cli --configuration Release --no-build -- call < examples/09-erase.json
```

Erasure retains the case reference and revision 9. Every historical snapshot for that
case is erased from the live database; ledger metadata remains. This does not delete WAL, backups or copies elsewhere.
Fixed example identities deliberately support exact replay in one fresh installation.
Do not change their request bytes to retry an uncertain operation.

A structured reply has exactly `endpoint`, `operationId`, `delivery` and `result`.
The first two are nullable when no request was admitted. Delivery is `NOT_SENT`,
`DELIVERED` or `UNCERTAIN`; the result contains the typed outcome, host failure or local failure.
Delivery does not establish acceptance; inspect the typed outcome in `result`.
Host failures carry `code` and `phase`; HTTP status comes from the response.

An uncertain mutation exits 4 and writes the exact request frame as the last stderr line.
Retain that frame privately and pass it unchanged to `call --retry`. A later `NOT_STARTED`,
ordinary refusal, failure or observation cannot disprove an earlier commit. Only a correlated
acceptance or exact replay resolves uncertainty about this request. Discarding it does not cancel
the operation. `operation.observe` reports retained acceptance metadata and cannot prove that
pending content matches it. See the [delivery contract](../docs/api.md).

The service protects admitted submissions before new acceptance. After a process restart, use
`operation pending --limit 50` to list your currently visible recovery metadata. Listing does not execute
anything or expose a saved request body. Select an item and deliberately run
`operation retry RECOVERY_ID OPERATION_ID`, using both original identifiers from that item. The server
reauthorizes the retained request; the CLI checks the selected target and receipt before resolving uncertainty.

After a correlated known result reaches its output sink, the CLI best-effort acknowledges the retained copy.
Use `operation forget RECOVERY_ID` for an explicit acknowledgement or deliberate abandonment; forgetting,
absence and expiry never prove that the business operation did not commit. Recovery expires after seven days
and has bounded capacity. Erasure removes every live recovery copy indexed to the case, including its own
submission, so a lost erasure reply may remain unresolved. See the [recovery contract](../docs/architecture.md#cc-app-003)
for these limits and the [complete CLI syntax](../docs/api.md#cc-cli-001).

Published acceptance derives all nine human mutation invocations from these independent frames,
checks exact dry-run equality, and executes them through the published CLI with fresh
Testcontainers PostgreSQL and Keycloak. It verifies receipts, replay, the final case,
erased history and `ClaimCore.Database verify --expect` against an accepted position.
