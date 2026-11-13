# Synthetic case walkthrough

Use a fresh installation and matching service/CLI. [Getting started](../docs/getting-started.md) supplies
local HTTPS, identity and owner setup. Configure the CLI using
[CLI use](../docs/integrating.md#cli-use); it receives no database credential.
Use synthetic data only. The selected account needs Case handler and Payment recorder for all cases;
Owner alone supplies no case rights.

From the repository root, send the first six changes in order:

```sh
claimcore call < examples/01-open.json
claimcore call < examples/02-decide.json
claimcore call < examples/03-record-payment.json
claimcore call < examples/04-close.json
claimcore call < examples/05-reopen.json
claimcore call < examples/06-revise-decision.json
```

Each successful change returns ACCEPTED and exit 0. The case ends at version 6, open, with EUR 750 paid against EUR 800 payable. No money is transferred or refunded. Human verbs use the same typed inputs; after step
three, inspect the next frame offline with:

```sh
claimcore case close DEMO-0001 --expected-revision 3 --dry-run
```

`help verbs` lists the grammar. `--preview` requests a review without recording; execution checks the
locked current state and rights again. Verbs default to text; `--format json` exposes the typed reply.

The remaining changes require a human Data steward grant:

```sh
claimcore call < examples/07-void.json
claimcore call < examples/08-reinstate.json
claimcore call < examples/09-erase.json
```

The erased case retains its ID, reference and version 9. Related live case/work/reminder payloads are
removed; change metadata remains. Erasure does not remove WAL, backups or external copies.

## Preserve the dispatched ID

The numbered files omit operationId. The CLI mints a UUIDv7 at dispatch, prints it to stderr and records
its identity. First execution is allowed for ten minutes; an accepted change remains replayable after
expiry while its retained content and current disclosure permit it.

For an exact retry, insert the printed ID as `input.operationId` and preserve every other input value.
Resending an ID-less file creates a separate operation. Do not infer an uncertain result from another
refusal or transport failure. Retain the exact dispatched frame until its answer is confirmed.

Reply frames contain endpoint, operationId, delivery and result. Delivery is NOT_SENT, DELIVERED or
UNCERTAIN; it describes transport progress, not acceptance. JSON calls return DATA projections for
automation. Human text verbs request HUMAN documents with the server's presentation Screen and typed
interaction context.

## Check an unconfirmed change

`claimcore operation get OPERATION_ID` returns an accepted receipt, a pending finalAt deadline, or final
NOT_ACCEPTED for your account after locked absence confirmation. `recovery list` and
`recovery check ATTEMPT_ID` use the current account/installation; add `--format json` for automation.
Interactive checks repeat pending answers until final. Restart recovery keeps identity only and cannot
recreate a business request. See [delivery recovery](../docs/integrating.md#recover-an-uncertain-change).
