# Service and CLI contract

[OpenAPI 3.1](../contracts/openapi.json) is the structural contract. Use `claimcore describe endpoints` and
`claimcore schema <endpoint> request|response` for the current registry rather than a second endpoint list.
`session` is GET; other endpoints are POST `/api/<endpoint>` (literal dots). Session bootstrap is anonymous;
case/authority work requires authentication and current grants. Every API call carries matching
`ClaimCore-Contract`; cookie POSTs also carry session antiforgery. POST bodies contain the input only, with
`application/json`; CLI frame wrappers belong to stdin.

Case and authority preview/execute share `operation.preview` / `operation.execute`, taking an input tagged
`scope: CASE|AUTHORITY`. Execute returns ACCEPTED, ALREADY_ACCEPTED, REJECTED, FAILED or OUTCOME_UNKNOWN;
preview returns REVIEWED, ALREADY_ACCEPTED, REJECTED or FAILED. Reads return SUCCEEDED, REJECTED or FAILED.
There is no wire CANCELLED outcome. Operation replies echo operationId; receipt scope carries case
reference/revision/act or authority target/act. Clients check those echoes, resource identity and promised
revision before trusting responses. ASCII case-reference folding permits case differences while preserving
accepted spelling. Observation checks its queried operation ID; grants responses check the requested actor ID.

Objects and tags are closed. Strict admission rejects invalid UTF-8/Unicode, duplicate decoded members, excess
nesting/bytes, unsafe/non-integer number lexemes and invalid technical IDs. Malformed business strings and
authored principal semantics reach Application/Domain and return REJECTED with field guidance, HTTP 200.
Principal refusals target issuer/subject; structural input errors remain INPUT_INVALID/400/NOT_STARTED.
Diagnostics carry code, action, target, actualRevision and limits; faults carry code/action. Neither contains
prose. Accepted response values obey permanent record formats, not today's admission policy. Clients decode
bounded strict bytes and complete schemas.

Host responses wrap `{ "hostFailure": { "code": ..., "phase": ... } }`, with HTTP status constrained to the
Contract table. NOT_STARTED means this attempt did not dispatch; STARTED_UNCONFIRMED or a null phase cannot
establish rejection. Missing/mismatched contract identities refuse before dispatch. The fixed `HTTP <status>`
response descriptions satisfy OpenAPI 3.1; localized prose stays outside it.

Contract identity is lowercase SHA-256 of canonical derived OpenAPI, computed in-process and shared by Web,
CLI and generated TypeScript. It identifies wire structure, not review, authority or freshness.
Locale/help/presentation-only edits leave it unchanged. Deploy service, CLI and browser together; there is no
compatibility endpoint. The generator tracks exactly [openapi.json](../contracts/openapi.json),
[client.ts](../contracts/client.ts) and [descriptors.json](../contracts/descriptors.json).
[Development](development.md#generated-contracts-and-boundary-verification) owns generation/freshness.

Pages independently authorize. Follow non-null nextCursor even after short pages; only null ends a view.
`QUERY_CURSOR_INVALID` supports a local fresh reload without implying lost authority. Both history details and
actor/grant pages allow up to 50 rows. Complete replies are bounded to 4 MiB. History snapshot disclosure
follows [current authority](architecture.md#cc-auth-001).

<a id="cc-cli-001"></a>

### CC-CLI-001 — Authenticated frames and exact delivery

`claimcore` denotes the published CLI; development uses `dotnet run --project src/ClaimCore.Cli --`. Human
verbs derive from the endpoint registry, field table and embedded descriptors:

```sh
claimcore case open DEMO-0001 --incident-date 2026-09-01 --incident-notification-date 2026-09-02 \
  --incident-country LV --claimant-name 'Synthetic claimant' --insurer-name 'Synthetic insurer' \
  --claimed-amount 100.00 --claimed-currency EUR --dry-run
claimcore case get DEMO-0001
claimcore authority list
claimcore operation observe 10000000-0000-4000-8000-000000000001
claimcore describe commands OPEN --format text
claimcore schema operation.execute request
```

Mutations mint a UUID once unless `--operation-id` is explicit. Every case act except open requires
`--expected-revision`; the CLI never fetches/rebases it. `--preview` requests advisory review. `--dry-run`
prints the exact call frame without configuration/authentication. Business strings are not trimmed, normalized
or validated by the CLI. Unknown, duplicate and missing flags refuse atomically with exit 64, empty stdout and
safe corrective help; no authored token is echoed. Text is the verb default; `--format json` selects the reply
frame. Language defaults to en; `--language en|lv|ar` selects authored human text. No TTY detection or
prompting changes output.

`call` reads one strict JSON `{ "endpoint": ..., "input": ... }` document (up to 128 KiB); `session` reads one
frame per NDJSON line and reuses process-local authentication. Reply frames have endpoint, operationId,
delivery (`NOT_SENT|DELIVERED|UNCERTAIN`) and result. Result is a typed service reply, hostFailure wrapper or
localFailure wrapper. Offline `help`, `describe`, `schema`, `version` and dry-run require no credentials. Help
topics describe settings, framing, delivery and examples; exact `--help`/`-h` aliases run before
stdin/configuration/transport. Local failure details are closed safe codes/parameters, never parser paths or
provider exceptions.

Configure HTTPS calls with:

| Setting | Meaning |
| --- | --- |
| CLAIMCORE_SERVICE_URL | Canonical HTTPS service root ending `/` |
| CLAIMCORE_OIDC_ISSUER | Canonical HTTPS issuer |
| CLAIMCORE_OIDC_CLIENT_ID | Registered identity-provider client |
| CLAIMCORE_CLI_AUTH_MODE | interactive (human PKCE) or automation (client credentials) |
| CLAIMCORE_OIDC_CLIENT_SECRET_FILE | Automation-only private secret file |
| CLAIMCORE_CLI_SERVICE_TRUST_ROOT_FILE | Optional local service PEM trust root |
| CLAIMCORE_CLI_OIDC_TRUST_ROOT_FILE | Optional local issuer PEM trust root |

System trust is default; explicit local roots preserve validity/hostname checks. Files obey [private
admission](operations.md#private-configuration); put no secrets in argv or reports. Interactive authentication
opens a system browser and accepts one PKCE callback on an ephemeral 127.0.0.1 port. Service/issuer traffic
remains HTTPS. Tokens remain process-local. Automation requires a confidential client restricted to client
credentials and a registered/granted SERVICE actor; see [provider
restrictions](operations.md#access-review-and-sessions).

<a id="cc-cli-003"></a>

### CC-CLI-003 — Mutation uncertainty after dispatch

Exits: 0 success, 2 rejection/protocol refusal, 3 definite failure, 4 uncertain mutation, 64 usage, 70
internal, 130 cancellation. Session stops on 4/130 or an oversized frame. On exit 4 the last stderr line is
the exact prepared call frame. Retain it and retry with `claimcore call --retry`; this retains earlier
uncertainty across process boundaries. Preparation compacts the admitted JSON once while preserving exact
strings, amount spelling, reference, ID and revision; all sends/retries use those frozen bytes. Output failure
after dispatch preserves exit 4. If both sinks fail, only the exit can remain available. A later NOT_STARTED,
REJECTED or FAILED result cannot resolve earlier uncertainty. Only correlated ACCEPTED/ALREADY_ACCEPTED
confirms exact pending content. Never replace its operation ID or edit it as a retry. The service stores
accepted operations only; no pending/recovery bundle exists. Complete writes/newline/flush establish OS
acceptance, not downstream consumption or persistence. No consumer-acknowledgement protocol exists.
Remote/private-file operation is qualified on macOS/Linux; offline discovery remains available elsewhere.
