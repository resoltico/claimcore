# Service and CLI contract

The [endpoint catalogue](../contracts/catalog.json) defines eleven endpoints, their strict wire
schemas and one shared fingerprint. `session` is GET; the others are POST to the literal
`/api/<endpoint>` path, including dots in names such as `/api/case.execute`. Session bootstrap is
anonymous; remaining operations require an authenticated principal. Case and authority work also
require the current ClaimCore actor grants. Every API request carries the matching
`ClaimCore-Contract` header.
Missing or different fingerprints are refused before dispatch. Deploy matching service, CLI and
browser artifacts.

An HTTP POST body is the endpoint's input object with media type `application/json` and must meet
the [host request limit](web.md#cc-web-001). The
`endpoint`/`input` frame wrapper below belongs to CLI stdin. Cookie-authenticated POST requests
also carry the antiforgery token returned by `session`; bearer calls authenticate separately.

| Endpoint            | Responsibility                                           |
| ------------------- | -------------------------------------------------------- |
| `session`           | Authentication state, fingerprint and antiforgery token  |
| `session.logout`    | End the browser session                                  |
| `context`           | Runtime context and installation capability advice       |
| `case.preview`      | Advisory review of a business or stewardship act         |
| `case.execute`      | Execute one exact case act                               |
| `case.get`          | Current actor-visible record                             |
| `case.list`         | Actor-visible bounded page                               |
| `case.history`      | Summary or retained full history page                    |
| `operation.observe` | Accepted case or authority evidence visible to the actor |
| `authority.execute` | Register actor, set grant or set enabled state           |
| `authority.list`    | Installation authority review with pagination            |

Case preview/execute inputs carry `operationId`, `caseReference`, integer `expectedRevision` and
a tagged `act`. Authority execution carries `operationId`, `target` principal and a tagged `act`.
The two execute endpoints use `ACCEPTED`, `ALREADY_ACCEPTED`, `REJECTED`, `FAILED`, `CANCELLED`
and `OUTCOME_UNKNOWN`. A refusal records nothing. Preview returns `REVIEWED`, `ALREADY_ACCEPTED`
or an explicit refusal/failure; it never
accepts a new operation. Reads, including operation observation, return `SUCCEEDED` or an explicit
refusal/failure. Absent and inaccessible resources share `RESOURCE_UNAVAILABLE`.

Case receipts retain the original acceptance metadata. Their snapshots are `DISCLOSED`,
`WITHHELD` or `ERASED` under current access; replay does not grant historical snapshot access.

Schemas close objects and tags and check permanent format invariants. Business admission policy
is evaluated only by Domain on newly asserted values, preserving old accepted text. Strict codecs
reject duplicate members, invalid UTF-8, malformed scalars, invalid UUID/revision values and excess
nesting/bytes. Wire-format failures are host refusals before Domain validation; they do not
produce business-field rejections. Typed endpoint outcomes use HTTP 200; host failures carry
their own HTTP status and an optional `executionPhase`. Server responses undergo full schema validation;
browser guards check endpoint, outcome and required consumed shapes. Malformed mutation replies
retain exact delivery uncertainty.

Tracked [contract artifacts](../contracts/catalog.json) are generated from F# contract definitions.
The fingerprint covers canonical framed catalogue metadata without its identity field, all schemas,
descriptors and default English; derived TypeScript, corpus and the fingerprint itself are excluded.
[Development](development.md) owns generation and freshness checks.

<a id="cc-cli-001"></a>

### CC-CLI-001 — Authenticated frames and exact delivery

The CLI calls HTTPS with authorization-code/PKCE or client credentials and receives no database
credential. `call` reads one `{"endpoint":...,"input":...}` JSON document from stdin, which may
span multiple lines. `session` reads one complete frame per line as NDJSON and reuses its
process-local authentication session. Each input frame is limited to 128 KiB; responses are
limited to 16 MiB. Input is strict UTF-8 and replies are validated against the generated contract.
Preview is advisory; execution revalidates locked state.

Discovery needs no credentials:

```sh
dotnet run --project src/ClaimCore.Cli --configuration Release --no-build -- help
dotnet run --project src/ClaimCore.Cli --configuration Release --no-build -- describe endpoints case.execute
dotnet run --project src/ClaimCore.Cli --configuration Release --no-build -- schema case.execute request
```

Configure calls with these environment variables:

| Variable                                | Value                                                                                    |
| --------------------------------------- | ---------------------------------------------------------------------------------------- |
| `CLAIMCORE_SERVICE_URL`                 | Canonical absolute HTTPS service root ending in `/`.                                     |
| `CLAIMCORE_OIDC_ISSUER`                 | Canonical absolute HTTPS issuer URL.                                                     |
| `CLAIMCORE_OIDC_CLIENT_ID`              | The identity provider's client ID.                                                       |
| `CLAIMCORE_CLI_AUTH_MODE`               | `interactive` for a human or `automation` for client credentials.                        |
| `CLAIMCORE_OIDC_CLIENT_SECRET_FILE`     | Required only in automation mode: absolute path to the owner-private client-secret file. |
| `CLAIMCORE_CLI_SERVICE_TRUST_ROOT_FILE` | Optional absolute private PEM root path for a local evaluation service.                  |
| `CLAIMCORE_CLI_OIDC_TRUST_ROOT_FILE`    | Optional absolute private PEM root path for a local evaluation issuer.                   |

System TLS trust is the default. Explicit trust-root files are accepted only for local endpoints;
certificate validity and hostname checks remain enabled. Secret and trust-root files use the
[private-input admission rules](database.md#cc-db-002) on macOS and Linux. Keep secret
contents out of command arguments, environment variables and reports.

Interactive mode opens the system browser for each new CLI process; the callback uses one-use
HTTP on loopback while issuer and service traffic remains HTTPS. Automation uses a confidential
client restricted to client credentials and an explicitly registered/granted SERVICE actor.
Apply the [identity-provider flow restrictions](web.md) and follow [Getting started](getting-started.md)
for the isolated human-owner flow. Tokens remain process-local.

<a id="cc-cli-003"></a>

### CC-CLI-003 — Mutation uncertainty after dispatch

Exit 0 is success; 2 is protocol/business refusal; 3 is definite local/service failure;
4 is dispatched mutation with uncertain delivery; 130 is cancellation. Invocation/root failures
use 64/70. An uncertain retry retains operation ID, expected revision and exact request bytes.
`session` stops on 4 or 130 and on an oversized frame; it cannot let a later result mask uncertainty.
Host failures carry an HTTP status and an optional `executionPhase`. `NOT_STARTED` means no
dispatch; `STARTED_UNCONFIRMED` preserves mutation uncertainty. A null phase does not establish
rejection. `CONTRACT_MISMATCH` is `NOT_STARTED`; reload before authoring a different request.
No pending request is stored by the service.
