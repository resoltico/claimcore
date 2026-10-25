# Service and CLI

Use a matching service, browser and CLI. [OpenAPI](../contracts/openapi.json) defines the wire shape and delivery
effect of each operation. Use the [generated CLI reference](cli.md) for verbs and choices.
`claimcore describe case open` describes one operation offline; `claimcore describe --format json` returns
the catalog entries, and `claimcore schema ENDPOINT request|response` shows a wire schema without credentials.

## HTTPS requests and replies

Session bootstrap is GET; other endpoints use POST `/api/<endpoint>` with literal dots. API calls carry
matching `ClaimCore-Contract`; cookie POSTs also carry antiforgery. A POST body is the input itself with
`application/json`, bounded to 64 KiB. Frame wrappers belong to CLI stdin. Authentication and current ClaimCore grants are separate.

Case/authority input uses `scope: CASE|AUTHORITY` at operation.preview and operation.execute. HTTP reply bodies
contain `outcome`; preview and execute also echo `operationId`. They have no CLI `endpoint` or `delivery` wrapper.
Receipts share operation and ledger metadata, with case or authority details in the tagged `scope` object.

| Operation | Outcomes |
| --- | --- |
| Execute | ACCEPTED, ALREADY_ACCEPTED, REJECTED, FAILED, OUTCOME_UNKNOWN |
| Preview | REVIEWED, ALREADY_ACCEPTED, REJECTED, FAILED |
| Read | SUCCEEDED, REJECTED, FAILED |
| Resolve | ACCEPTED with receipt, NOT_ACCEPTED with null data, REJECTED, FAILED |

Resolve gives the author a final answer: a receipt, or a permanent actor/operation fence preventing delayed
acceptance. Repeat resolve after a failed response. An author without current read rights receives WITHHELD;
erased snapshots report ERASED. Observation is a read of accepted metadata; its absence is not final.

Clients validate complete bounded responses and correlation before trusting them. Operation replies echo the
operation ID; receipts identify case/revision/act or authority target/act. Observation checks the requested ID;
grant pages check the requested actor. Case lookup permits ASCII reference-case differences while preserving
accepted spelling.

Objects/tags are closed. Strict admission rejects malformed UTF-8/Unicode, duplicate decoded members, excessive
bytes/depth, invalid technical IDs and unsafe numbers. Malformed business strings reach Application/Domain
and return typed REJECTED guidance with HTTP 200. Structural errors remain host refusals. Diagnostics carry
codes, targets, actions and limits, with no server prose.

Host failures use `{ "hostFailure": { "code": ..., "phase": ... } }` at the status declared by Contract.
NOT_STARTED says only that this attempt did not dispatch. STARTED_UNCONFIRMED or null phase cannot establish
rejection of an earlier attempt. Accepted values follow permanent formats, independently of current admission.

Contract identity is SHA-256 of canonical derived OpenAPI. It identifies wire structure; locale and help-only
changes leave it unchanged. The tracked contract artifacts are [openapi.json](../contracts/openapi.json),
[client.ts](../contracts/client.ts), [catalog.json](../contracts/catalog.json) and
[messages.ts](../contracts/messages.ts). The same generator
also owns [the CLI reference](cli.md). Catalog bodies define operations once; paths, branches, flags and
synopses are derived from them.
[Development](development.md#generated-contracts-and-boundary-verification) explains regeneration and checks.

Pagination independently authorizes each page. Follow non-null nextCursor even after a short page; null ends
the view. Invalid cursor guidance supports a local fresh load. History and actor/grant pages allow up to fifty
rows, and complete replies are bounded to 4 MiB. [Disclosure](architecture.md#cc-auth-001) applies to history too.

<a id="cc-cli-001"></a>

### CC-CLI-001 — Authenticated frames and exact delivery

`claimcore` denotes the published CLI apphost. A solution build prints the directory containing
`claimcore.dll`; run its matching `claimcore` apphost or add that directory to `PATH`.
Start with offline `help`, `help verbs`, `help configuration`, `help call` and `help delivery`.

```sh
claimcore case open DEMO-0001 --incident-date 2026-09-01 --incident-notification-date 2026-09-02 \
  --incident-country LV --claimant-name 'Synthetic claimant' --insurer-name 'Synthetic insurer' \
  --claimed-amount 100.00 --claimed-currency EUR --dry-run
claimcore case get DEMO-0001
claimcore authority list
claimcore operation resolve 10000000-0000-4000-8000-000000000001
claimcore describe case open
```

A new mutation mints one UUID unless `--operation-id` is explicit. Case acts after opening require
`--expected-revision`; CLI never fetches/rebases it. `--preview` asks for advisory review. `--dry-run` prints
the exact frame without configuration or authentication. Business strings are preserved. Unknown, duplicate
or missing flags give exit 64, empty stdout and safe help without echoing authored tokens.

Human verbs default to text; `--format json` selects a reply frame. `--language en|lv|ar` selects authored
human text. There is no prompting or TTY-based output change. Display warnings expose suspicious Unicode
without changing values or JSON replies.

`call` reads one strict `{ "endpoint": ..., "input": ... }` document up to 128 KiB. `session` reads one frame
per NDJSON line and reuses process-local authentication. Reply frames contain endpoint, operationId, delivery
and result. Delivery is NOT_SENT, DELIVERED or UNCERTAIN; result is a typed service, host or local failure reply.
Use the typed outcome to establish acceptance. Help aliases run before stdin, configuration or transport.

Configure CLAIMCORE_SERVICE_URL, CLAIMCORE_OIDC_ISSUER, CLAIMCORE_OIDC_CLIENT_ID and CLAIMCORE_CLI_AUTH_MODE.
Interactive mode uses a public human PKCE client; automation uses client credentials and a private
CLAIMCORE_OIDC_CLIENT_SECRET_FILE. Optional service/OIDC trust-root files replace system roots while retaining
hostname/validity checks. `help configuration` owns the exact settings and causes. The
[walkthrough](getting-started.md#configure-the-human-owner-cli-and-grant-case-work) shows local trust setup.

Interactive login opens a browser with a one-use ephemeral 127.0.0.1 callback. Service/issuer traffic remains
HTTPS and tokens remain process-local. Automation requires a registered/granted SERVICE actor and the
[provider flow restrictions](operations.md#access-review-and-sessions). Never put secrets in argv or reports.

<a id="cc-cli-003"></a>

### CC-CLI-003 — Mutation uncertainty after dispatch

| Exit | Meaning |
| --- | --- |
| 0 | Success |
| 2 | Rejection or protocol refusal |
| 3 | Definite failure |
| 4 | Uncertain mutation |
| 64 / 70 / 130 | Usage / internal error / cancellation |

On exit 4, the last stderr line is the exact prepared call frame. Retain it privately and resend unchanged
with `claimcore call`, or use `claimcore operation resolve OPERATION_ID`. Resolve exits 0 for ACCEPTED,
2 for NOT_ACCEPTED or REJECTED, and 3 for FAILED. After final NOT_ACCEPTED, read current state before preparing
a new operation with a new ID.

A later ordinary execute refusal or fault cannot disprove the earlier commit. Accepted execution, exact replay
or a final resolution settles it. Session stops on 4/130 or an oversized frame. Output failure after dispatch
preserves exit 4. Complete writes/newline/flush establish OS acceptance, without proving downstream persistence.
Preparation compacts admitted JSON once while preserving strings, amount spelling, reference, ID and revision.
