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

Every output frame includes `attemptKnowledge`, `resolutionEvidence`, `endpoint` and
`operationId` (nullable when no request was admitted). Read `attemptKnowledge` together with
the process exit: `ACCEPTED` confirms exact submitted content; `UNCONFIRMED` requires preserving
the original frame and retrying it unchanged. The frame describes one CLI attempt. An automation
caller must retain earlier `UNCONFIRMED` knowledge across new processes: a later `NOT_SENT`,
`NOT_ACCEPTED`, read result or ordinary refusal cannot resolve that earlier attempt. Only correlated
`CONTENT_ACCEPTED` evidence from exact execute/preview replay confirms content; correlated
`CONTENT_EXCLUDED` proves that different content under the immutable ID was refused.
`operation.observe` reports retained acceptance metadata and cannot prove the pending content
matches it. Host failures retain their HTTP `status` and `executionPhase`; `NOT_STARTED` describes
that attempt alone. See the [delivery contract](../docs/api.md).

Published acceptance executes these same files through the published CLI with fresh
Testcontainers PostgreSQL and Keycloak. It verifies receipts, replay, the final case,
erased history and `ClaimCore.Database verify --expect` against an accepted position.
