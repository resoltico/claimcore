# Synthetic case walkthrough

Work through nine accepted changes on one synthetic case: open, decide, record payment, close, reopen,
clear the payment record, void, reinstate and erase. Use a fresh installation and matching service/CLI.
[Getting started](../docs/getting-started.md) creates the service, identity provider and protected key ring.
The CLI uses HTTPS and receives no database credential.

Use the public CLI profile from [getting started](../docs/getting-started.md#optional-cli-setup),
or configure the connection and authentication settings in [CLI use](../docs/integrating.md#cli-use).
Environment values override individual profile settings. For automation, supply
its private client-secret file. For local HTTPS certificates outside system trust, configure
the explicit CLI service and OIDC trust-root files. The selected actor needs installation `CASE_HANDLER` and `PAYMENT_RECORDER` grants.
An installation `OWNER` grants authority; that role alone does not grant case work.

From the repository root, execute the six business frames, in order:

```sh
claimcore call < examples/01-open.json
claimcore call < examples/02-decide.json
claimcore call < examples/03-record-payment.json
claimcore call < examples/04-close.json
claimcore call < examples/05-reopen.json
claimcore call < examples/06-correct-payment-record.json
```

The human verbs use the same typed inputs. For example, after step three, this prints the
same request frame as `04-close.json` without authentication or a network call:

```sh
claimcore case close DEMO-0001 --expected-revision 3 --operation-id 10000000-0000-4000-8000-000000000004 --dry-run
```

Use `help verbs` for the registry-derived grammar; omit `--dry-run` to execute, or send the
printed frame unchanged to `call`. Verbs default to localized text; `--format json` returns a
structured reply. The numbered JSON files remain independent, exact request examples.

Each returns exit 0 and `ACCEPTED`. `DEMO-0001` ends at revision 6, open, with its
payment date cleared. Payment facts are recorded assertions; no money is transferred.
While its payload is retained and current authority permits replay, an exact resend returns
`ALREADY_ACCEPTED` with the original acceptance metadata and currently authorized snapshot
disclosure. A preview is advisory; execution validates the locked current state.

The remaining three frames require a human `DATA_STEWARD` grant. They void the case
for an entry error, reinstate it and erase it:

```sh
claimcore call < examples/07-void.json
claimcore call < examples/08-reinstate.json
claimcore call < examples/09-erase.json
```

Erasure retains the CaseId, case reference and revision 9. Every historical snapshot for that
case is erased from the live database; ledger metadata remains. This does not delete WAL, backups or copies elsewhere.
Fixed example identities deliberately support exact replay in one fresh installation.
Do not change their request bytes when resending an uncertain operation.

A structured reply has exactly `endpoint`, `operationId`, `delivery` and `result`.
The first two are nullable when no request was admitted. Delivery is `NOT_SENT`,
`DELIVERED` or `UNCERTAIN`; the result contains the typed outcome, host failure or local failure.
Delivery does not establish acceptance; inspect the typed outcome in `result`.
Host failures carry `code` and `phase`; HTTP status comes from the response.

For interrupted submission, follow the [delivery recovery guide](../docs/integrating.md#delivery-recovery).

See the [glossary](../docs/glossary.md) for project terminology.
