# Service and CLI contract

The [OpenAPI 3.1 contract](../contracts/openapi.json) defines twelve endpoints and their strict wire
schemas. Service and clients share one consumer-contract identity, described below.
`session` is GET; the others are POST to the literal
`/api/<endpoint>` path, including dots in names such as `/api/case.execute`. Session bootstrap is
anonymous; remaining operations require an authenticated principal. Case and authority work also
require the current ClaimCore actor grants. Every API request carries the matching
`ClaimCore-Contract` header.
Missing or different contract identities are refused before dispatch. Deploy matching service, CLI and
browser artifacts.

An HTTP POST body is the endpoint's input object with media type `application/json` and must meet
the [host request limit](web.md#cc-web-001). The
`endpoint`/`input` frame wrapper below belongs to CLI stdin. Cookie-authenticated POST requests
also carry the antiforgery token returned by `session`; bearer calls authenticate separately.

| Endpoint            | Responsibility                                           |
| ------------------- | -------------------------------------------------------- |
| `session`           | Authentication state, contract identity and antiforgery token |
| `session.logout`    | End the browser session                                  |
| `context`           | Runtime context and installation capability advice       |
| `case.preview`      | Advisory review of a business or stewardship act         |
| `case.execute`      | Execute one exact case act                               |
| `case.get`          | Current actor-visible record                             |
| `case.list`         | Actor-visible bounded page                               |
| `case.history`      | Summary or retained full history page                    |
| `operation.observe` | Accepted case or authority evidence visible to the actor |
| `authority.execute` | Register actor, set grant or set enabled state           |
| `authority.list`    | Bounded installation actor summaries                     |
| `authority.grants`  | Bounded grant review for one selected actor              |

Paged reads accept a maximum `limit` from 1 through 50. `case.list` includes installation offers
from the same actor snapshot as that page's rows. Advice follows the latest successful page;
accumulated rows from successive requests are not one frozen snapshot. Execution rechecks authority.

`authority.list` returns actor ID, principal and enabled state, without nested grants. Use
`authority.grants` with `actorId`, optional `cursor` and `limit` to review that actor's grants.
Both reads require current installation ReadAuthority; disabled targets remain reviewable.
The grant page returns its current actor header and at most 50 grants. Continuations bind the endpoint and target resource, with no identity, authority or time binding.
Grants may evolve between requests; every page authorizes in its own read snapshot.
A successful reply must echo the requested actor ID in its actor header.
Use an actor ID returned by `authority.list` in the CLI frame (the ID below is synthetic):

```json
{
    "endpoint": "authority.grants",
    "input": { "actorId": "10000000-0000-4000-8000-000000000001", "limit": 50 }
}
```

`case.history` returns uniform SUMMARY or FULL entries. Both details allow pages of up to 50 entries.
The server can return fewer entries than requested. Only `nextCursor: null` establishes the end:
follow a non-null cursor even after a short page. This capacity does not change accepted text,
truncate history or reduce historical disclosure requirements.

Malformed business input and out-of-range page limits recommend `CORRECT_INPUT`. Revision
conflicts recommend `READ_CURRENT`, preserving the distinction between correcting a request
and refreshing state.

`QUERY_CURSOR_INVALID` recommends `READ_CURRENT`: discard the local continuation and load the
same view without a cursor. It does not by itself establish loss of authority. A fresh request
still rechecks current rights. Browser case, actor, selected-grant and history views offer explicit
reload; they never rebase or replace an uncertain mutation to recover a read.

Case preview/execute inputs carry `operationId`, `caseReference`, integer `expectedRevision` and
a tagged `act`. Authority execution carries `operationId`, `target` principal and a tagged `act`.
The two execute endpoints use `ACCEPTED`, `ALREADY_ACCEPTED`, `REJECTED`, `FAILED`, `CANCELLED`
and `OUTCOME_UNKNOWN`. A refusal records nothing. Preview returns `REVIEWED`, `ALREADY_ACCEPTED`
or an explicit refusal/failure; it never
accepts a new operation. Reads, including operation observation, return `SUCCEEDED` or an explicit
refusal/failure. Absent and inaccessible resources share `ACCESS_RESOURCE_UNAVAILABLE`.

Case receipts retain the original acceptance metadata. Their snapshots are `DISCLOSED`,
`WITHHELD` or `ERASED` under current access; replay does not grant historical snapshot access.

Schemas close objects and tags. Strict byte admission rejects duplicate decoded member names,
invalid UTF-8 and Unicode, numeric lexemes outside the safe integer profile, invalid technical
UUID/revision shapes, and excess nesting/bytes. Request business values remain strings: an invalid
business date, amount, currency, country, reference or asserted text reaches Application/Domain and
returns typed `REJECTED` with field guidance, HTTP 200 and CLI exit 2. A shape error remains a
host `INPUT_INVALID` / HTTP 400 / `NOT_STARTED` refusal. Accepted response values obey permanent
record formats; today's admission policy never invalidates retained history. Web encodes the typed
reply directly. The CLI decodes with the Wire codec and the browser interprets the generated
OpenAPI component schemas from bounded strict bytes. Malformed mutation replies preserve exact
request identity and delivery uncertainty.

The runtime consumer-contract identity is lowercase SHA-256 of the canonical
[versioned input manifest](../contracts/contract-manifest.json). It covers wire shape, capability
and request bindings, all authored locale resources, help and delivery/correlation specifications.
Descriptor-only, locale-only and policy-only changes therefore require matching clients.
Session discovery exposes `contractIdentity`; `ClaimCore-Contract` carries that identity.
The manifest excludes identity-bearing generated client/source resources to avoid self-reference.
[Development](development.md#generated-contracts-and-boundary-verification) owns generation and
freshness checks. The [descriptors](../contracts/descriptors.json) carry presentation and typed advice
vocabularies. `messages` supplies core-authored default templates; descriptor label, meaning and
confirmation identities reference core resources. Request bindings are available in discovery and
[the generated binding document](../contracts/request-bindings.json). `context.views` advertises `CASES`, `AUTHORITY` and `OPERATIONS`; installation offers
contain only OPEN or an authority act. A case record has `advice.acts` and `advice.history`; correction
choices belong only to CORRECT_CASE. Execution always rechecks current authority.

Pagination uses [plain canonical keysets](architecture.md#cc-auth-001). Editing or sharing a cursor
conveys no access. Both history details allow 50 rows and complete client replies are bounded to
4 MiB; continuations remain required after short pages.

Case preview/execute and authority execute response envelopes echo the admitted request operation ID,
including refusals. Clients correlate that ID and receipt scope/act, case resource identity and promised
revision before using the reply. ASCII reference identity permits case differences without rewriting
accepted spelling. Observation correlates the queried ID but cannot prove pending request content:
only exact execute or preview replay confirms that content. The shared read correlation policy also
rejects a schema-valid authority.grants reply for another actor. A specifically correlated idempotency
conflict proves CONTENT_EXCLUDED; other refusals do not resolve earlier uncertainty.

Principal wire syntax is deliberately generic Unicode text plus HUMAN/SERVICE kind. Canonical issuer
and kind-specific identity admission remain Application-owned and run in Hosting before ledger dispatch;
invalid identities return INPUT_INVALID/400/NOT_STARTED after authentication. The CLI renders that
host failure with exit 3. Decoding wire syntax never promises principal admission.

Deploy matching service, CLI, browser assets and generated artifacts together. Integrations must use
the generated types and frame format for that contract identity and follow grant/history continuations
rather than assuming a requested page size is returned. The service accepts one contract; it provides
no compatibility endpoint or alternative parser. Installation admission follows the
[database contract](database.md#cc-db-001).

<a id="cc-cli-001"></a>

### CC-CLI-001 — Authenticated frames and exact delivery

The CLI calls HTTPS with authorization-code/PKCE or client credentials and receives no database
credential. `call` reads one `{"endpoint":...,"input":...}` JSON document from stdin, which may
span multiple lines. `session` reads one complete frame per line as NDJSON and reuses its
process-local authentication session. Each input frame is limited to 128 KiB; responses are
limited to 4 MiB. Input is strict UTF-8 and replies are validated against the generated contract.
Preview is advisory; execution revalidates locked state.

Discovery needs no credentials. `help [topic] [--language en|lv|ar]` is plain UTF-8 text,
with English as the culture-independent default. Topics include `configuration`, `call`, `session`,
`delivery`, `examples`, `describe` and `schema`. Exact `--help`, `-h`, `<command> --help` and
`<command> -h` aliases run before stdin, configuration or transport. Aliases accept no extra flags;
use `help call --language lv` for localized help.

`describe` defaults to JSON. Add `--format text` for a concise static descriptor projection;
only help and explicit text describe admit `--language`. Selected human descriptions include
input field IDs with localized labels, prefill sources, correction choices/replacement fields, and
endpoint mutation nature plus exact request/response schema commands. Schemas remain JSON.
Duplicate/unknown
options and selectors refuse atomically with safe corrective commands on stderr, empty stdout
and exit 64. Unknown authored tokens are never echoed. No TTY-dependent format or prompt is used.

Local failure frames require a closed `diagnostic` object. Known configuration failures use
`CLI_CONFIGURATION_ISSUE` (also an alias of `help configuration`) with allowlisted `setting` and `issue` (`MISSING`, `INVALID` or
`PRIVATE_INPUT_REFUSED`); unknown local causes remain broad. Protocol failures carry
`CLI_PROTOCOL_GUIDANCE`: resolve that exact ID with `help CLI_PROTOCOL_GUIDANCE` or
`describe diagnostics`. Both project the same generated core guidance for `schema invocation`,
`schema response` and call/document versus session/NDJSON framing. No parser path, raw key, URL,
private filename, provider message or exception text
is included. Configuration is checked in service-root, issuer, client-ID, auth-mode, mode-dependent
secret-path, OIDC-trust-root, service-trust-root order; file admission retains its known cause.

Authored principal refusals remain HTTP 400 / `INPUT_INVALID` / `NOT_STARTED`, using the same
host-failure codec with `PRINCIPAL_INPUT_INVALID`, closed `target` (`issuer`, `subject`, `kind`)
and `issue` (`MISSING`, `INVALID`). Other host status/code/phase combinations refuse that detail.
Authentication and protected-resource failures remain generic.

`describe authority [kind]` exposes the Application-owned
authority definitions alongside case command discovery:

```sh
dotnet run --project src/ClaimCore.Cli --configuration Release --no-build -- help
dotnet run --project src/ClaimCore.Cli --configuration Release --no-build -- describe endpoints case.execute
dotnet run --project src/ClaimCore.Cli --configuration Release --no-build -- describe endpoints authority.grants
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

The CLI uses Duende.IdentityModel and Duende.IdentityModel.OidcClient for discovery and token
acquisition. Default discovery policy requires HTTPS and validates issuer and endpoint base
addresses; endpoints outside the issuer base URL are refused. Interactive mode opens the system
browser for each new CLI process, uses PKCE S256, and accepts one callback on an ephemeral
`127.0.0.1` port. HTTP is confined to that callback; issuer and service traffic use HTTPS.
Pushed authorization requests are used when advertised by the provider. The CLI consumes only
the access token; it disables userinfo loading and makes no decisions from identity-token claims.
Automation uses a confidential client restricted to client credentials and an explicitly
registered/granted SERVICE actor. ClaimCore bounds process-local token reuse using elapsed
monotonic time and conservative UTC expiry measured from token dispatch.
Apply the [identity-provider flow restrictions](web.md) and follow [Getting started](getting-started.md)
for the isolated human-owner flow. Tokens remain process-local.

<a id="cc-cli-003"></a>

### CC-CLI-003 — Mutation uncertainty after dispatch

Exit 0 is success; 2 is invocation/protocol or typed business refusal (including malformed business
strings); 3 is definite local/service failure;
4 is dispatched mutation with uncertain delivery; 130 is cancellation. Invocation/root failures
use 64/70. An uncertain retry retains operation ID, expected revision and exact request bytes.
`session` stops on 4 or 130 and on an oversized frame; it cannot let a later result mask uncertainty.
Host failures carry an HTTP status and an optional `executionPhase`. `NOT_STARTED` means no
dispatch; `STARTED_UNCONFIRMED` preserves mutation uncertainty. A null phase does not establish
rejection. `CONTRACT_MISMATCH` is `NOT_STARTED`; reload before authoring a different request.
No pending request is stored by the service.

Every CLI reply explicitly carries per-invocation `attemptKnowledge`, endpoint/operation identity
when available, resolution evidence and the typed/host/local result. Host status and execution phase
are retained. NOT_SENT, READ_SUCCEEDED, READ_FAILED, ACCEPTED, NOT_ACCEPTED and UNCONFIRMED
are client delivery knowledge; server OUTCOME_UNKNOWN still means lost commit confirmation.
The automation caller retains prior unresolved knowledge and the original request across processes.
A later pre-dispatch failure, authorization refusal or NOT_STARTED result cannot resolve its earlier
uncertainty. The CLI stores no cross-process pending state. Output failure after a dispatched mutation
uses exit 4 when confirmation could not be safely delivered; read/pre-dispatch output failures do not.
If both output streams fail, only the nonzero exit can remain available. On qualified macOS/Linux,
one output strategy is selected per inherited sink before writing and retained for the invocation:
nonseekable pipes/terminals use a borrowed managed stream that surfaces native failures; seekable
files retain Console's native shared-offset/append behavior. There is no error fallback that hides
EPIPE. A complete frame, newline and successful flush establish OS acceptance of those writes;
they cannot establish that a downstream process consumed, parsed or persisted the bytes. No
consumer acknowledgement protocol is supplied. Windows remote/private-file execution is unqualified;
offline discovery remains available.
