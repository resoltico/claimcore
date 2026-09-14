# ClaimCore Web

`ClaimCore.Web` is the normal human interface to the typed `IClaimsCore` facade. It serves a React
application from a local HTTPS F# host and is neither an internet-facing service nor a multi-user
identity system.
Private-file host startup is supported on macOS and Linux; Windows builds and configuration-free
discovery work, but runtime startup fails closed until verified Windows private-file support exists.

## Web executable

The generated block below is synchronized with the compiled host. Discovery does not load private
configuration or open PostgreSQL.

<!-- generated:begin web-help -->
```text
ClaimCore.Web 0.2.0 — local HTTPS human interface
  ClaimCore.Web                 Start the configured loopback host
  ClaimCore.Web help            Show this configuration-free help
  ClaimCore.Web version         Show the compiled product version
  ClaimCore.Web version --json  Show compiled release identity as JSON
Required private paths:
  CLAIMCORE_CONNECTION_FILE
  CLAIMCORE_WEB_CERTIFICATE_PATH
  CLAIMCORE_WEB_STATE_DIR
Optional tightening settings:
  CLAIMCORE_WEB_ORIGIN                 HTTPS localhost origin; default https://localhost:5443
  CLAIMCORE_WEB_MAX_JSON_BYTES         1..65536; default 65536
  CLAIMCORE_WEB_CORE_PERMITS           1..4; default 4
  CLAIMCORE_WEB_CORE_QUEUE             1..16; default 16
  CLAIMCORE_WEB_LOGIN_PERMITS          1..5 per minute; default 5
  CLAIMCORE_WEB_SESSION_IDLE_MINUTES   1..30; default 30
  CLAIMCORE_WEB_SESSION_ABSOLUTE_MINUTES 1..480; default 480 and not below idle
```
<!-- generated:end web-help -->

## Publish and start

[Getting started](getting-started.md) owns the complete source-to-first-login sequence. Build Web
assets once with the locked Node 26 frontend, then publish `ClaimCore.Web`. Publication verifies the
asset manifest against exact source, npm lock, generated Web-v2 contract, Node/npm toolchain, notices,
and output bytes before placing assets under `wwwroot`. Ordinary .NET builds never invoke npm.

Start the published assembly with private absolute paths:

```sh
CLAIMCORE_CONNECTION_FILE=/absolute/private/path/app.connection \
CLAIMCORE_WEB_CERTIFICATE_PATH=/absolute/private/path/claimcore-web.pfx \
CLAIMCORE_WEB_STATE_DIR=/absolute/private/path/web-state \
dotnet /absolute/path/publish/ClaimCore.Web.dll
```

The certificate must be owner-private blank-password PKCS#12/PFX with a `localhost` private key. One
host exclusively leases a state directory, creates one random owner-private bootstrap credential,
and prints only the login URL and credential-file path. A restart revokes all sessions and issues a
new bootstrap credential; it does not preserve browser admission.

## Configuration reference

| Variable | Requirement and bound |
|---|---|
| `CLAIMCORE_CONNECTION_FILE` | Required absolute path to a regular, non-linked, strict UTF-8 runtime-role connection file of 1–8,192 bytes; owner-only on POSIX. |
| `CLAIMCORE_WEB_CERTIFICATE_PATH` | Required absolute path to a regular, non-linked blank-password PFX with a private key, at most 8 MiB; owner-only on POSIX. |
| `CLAIMCORE_WEB_STATE_DIR` | Required absolute owner-private directory; the host creates it as mode `0700` on POSIX and rejects a linked/reparse directory. |
| `CLAIMCORE_WEB_ORIGIN` | Optional exact HTTPS `localhost` origin with no path, user information, query, or fragment; defaults to `https://localhost:5443`. |
| `CLAIMCORE_WEB_SESSION_IDLE_MINUTES` | Integer 1–30; defaults to 30. |
| `CLAIMCORE_WEB_SESSION_ABSOLUTE_MINUTES` | Integer 1–480, not less than idle lifetime; defaults to 480. |
| `CLAIMCORE_WEB_MAX_JSON_BYTES` | Integer 1–65,536; defaults to 65,536. |
| `CLAIMCORE_WEB_CORE_PERMITS` | Integer 1–4; defaults to 4. |
| `CLAIMCORE_WEB_CORE_QUEUE` | Integer 1–16; defaults to 16. |
| `CLAIMCORE_WEB_LOGIN_PERMITS` | Integer 1–5 per fixed one-minute window; defaults to 5. |

The host listens only on loopback at the configured port and requires exact Host admission. It sets a
131,072-byte Kestrel ceiling solely for the largest raw envelope import; every endpoint applies its
own lower limit before allocation. `GET /health/live` is a loopback host liveness probe, not an
authenticated readiness, database-integrity, migration, or backup check.

## Web-v2 contract and admission

<a id="cc-web-001"></a>
### CC-WEB-001 — Exact Web-v2 admission, routes, and typed outcomes

The pure `ClaimCore.Contracts` projection owns the canonical Web-v2 endpoint catalog, exact request
and response schemas, raw-media rules, deterministic response codecs, split TypeScript DTO modules,
parsed conformance corpus, and Web wire fingerprint. The F# host consumes the same catalog and codec
authority. A deterministic Node postprocess compiles the aggregate response schema into strict AJV
standalone core and recovery validator groups and a typed asynchronous selector. The selector loads
only the group needed to validate the endpoint response. Run `npm --prefix web run contract:check` to
regenerate and compare every checked artifact; do not hand edit generated contract files.
During the pre-1.0 source preview, `/api/v2` identifies this route and admission family, not a
promise that every response shape remains backward-compatible. The host and bundled browser must
agree on the exact Web fingerprint before claimant-bearing responses are accepted.

Before the host admits requests, shared HostSecurity checks physical credential, certificate, state,
and lock paths through opened handles. Existing broad-mode, extended-ACL, or linked files are refused
without chmod or redirected creation; bootstrap rotation removes only validated private generated
credentials.
Private-file startup fails closed on Windows rather than guessing an equivalent ACL check.
Configuration-free `help` and `version` remain available before private admission; unsupported
process arguments fail with a usage refusal instead of starting the host.

Only `/api/v2` is an API surface. `/api/v1` and every unknown `/api/*` request return a typed,
no-store `404` and never fall through to the single-page application. Business and Application
outcomes are HTTP `200` endpoint bodies. Typed host failures use the appropriate `400`, `401`, `403`,
`404`, `409`, `413`, `415`, `429`, `500`, or `503` response and state
`executionPhase: NOT_STARTED` or `STARTED_UNCONFIRMED` where meaningful.

| Method and path | Core mapping |
|---|---|
| `GET /api/v2/session` | Anonymous or current `SessionSnapshot` |
| `POST /api/v2/session/login` | Bootstrap admission and authenticated snapshot |
| `POST /api/v2/session/logout` | Revoke session and return fresh anonymous snapshot |
| `GET /api/v2/definition` | `Describe` with separate semantic and Web fingerprints |
| `POST /api/v2/cases/get`, `/list`, `/history` | `Get`, `List`, `History` |
| `POST /api/v2/operations/observe`, `/prepare`, `/submit` | `ObserveOperation`, `Prepare`, `Recovery.Resolve` |
| `POST /api/v2/recovery/list`, `/inspect`, `/resolve`, `/dismiss`, `/export` | `IClaimsCore.Recovery`; export requires the exact retained operation ID and SHA-256 digest. |
| `POST /api/v2/recovery/import-envelope/preview`, `/retain` | Envelope preview or retain |
| `POST /api/v2/recovery/import-record/preview`, `/retain` | Canonical-record preview or retain |

Every route first requires loopback and the exact configured Host. Static assets, health, and
`GET /api/v2/session` need no session. Login requires exact-origin/fetch admission, antiforgery, JSON
media, and a 4 KiB body but no prior session. Logout requires a current session, exact-origin/fetch
admission, antiforgery, and an exact empty JSON object no larger than 1 KiB. Definition requires a
current session. Other JSON case, operation, and recovery posts require a current session,
exact-origin/fetch admission, antiforgery, exact JSON, and at most 65,536 bytes.
Idle and absolute session expiry, bounded login attempts, malformed UTF-8, invalid request scalars,
and oversized bodies fail before core dispatch with safe no-store host outcomes. Once admitted, every
real route renders its endpoint-specific typed Application result through the Contracts codec.

Envelope import is raw `application/vnd.claimcore.recovery+json`, at most 131,072 bytes. Canonical
record import is raw `application/vnd.claimcore.canonical-command+json`, at most 65,536 bytes. Retain
resends the exact previewed bytes and supplies the generated exact source-digest header; artifacts are
not Base64-wrapped again.

The browser validates every JSON result against its exact endpoint schema, validates non-success
statuses against the closed host-failure contract, and checks the expected Web fingerprint before
accepting claimant-bearing data. Cross-endpoint and malformed values fail closed. The browser neither
reconstructs a transition nor gains access to a store, retained preparation, recovery port, or
transition callback.

An exact prepare retry after acceptance returns typed `OBSERVED_ACCEPTED` with the authoritative
receipt, not technical preparation details or a new advisory review. It remains available after the
accepted preparation is pruned. Exact retained material that is no longer reviewable returns
`RETAINED_FOR_RECOVERY` with a typed reason. Neither branch auto-submits; the operator follows the
exact recovery identity when needed.

An exact submit or resolve identity also observes the accepted receipt after pruning without a new
attempt; pruned technical material cannot be exported.

## Session and delivery safety

Logout is unavailable while a mutation is dispatched. When it succeeds, the host revokes the registry
entry, signs out the cookie, makes the current principal anonymous, issues a fresh anonymous
antiforgery token, and returns that `SessionSnapshot`. The browser increments its local session epoch,
aborts reads, and clears claimant state.

Reads may be abortable. Mutation HTTP never uses browser cancellation after dispatch. A typed
not-started admission response proves no execution. A lost prepare response is
`PREPARATION_UNKNOWN` and may be retried or inspected with the exact operation ID. A lost, timed-out,
malformed, wrong-media, or undecodable submit response after dispatch is
`STARTED_UNCONFIRMED`: preserve only operation ID, digest, and recovery direction, clear claimant
state, and never retry automatically.

## Recovery, downloads, and clipboard

The browser lists actionable pending recovery work by default and offers an explicit bounded terminal
view for accepted and revoked technical material. Detailed inspection pages identified attempts with
an opaque operation-bound cursor and includes independent legacy uncertainty only on demand. A
pruned revoked preparation is a payload-free tombstone, not a substitute for claimant data. The UI
can resolve or dismiss only after accessible confirmation using the exact operation ID and digest.
An accepted receipt is not offered as a new resolve action; an exact replay can observe it without a
second accepted revision. Reload clears browser review state; durable recovery is the Application
workflow, not browser state.

Export first warns the operator, then accepts only a bounded attachment named exactly
`claimcore-recovery-<canonical-operation-id>.json` with media type
`application/vnd.claimcore.recovery+json`; duplicate, mismatched UTF-8, suffix-spoofed, or inline
content dispositions fail closed. The file contains claimant data. Import uses file
selection, preview, confirmation, and retain of identical bytes; it never auto-submits.

Downloads and clipboard content leave ClaimCore's process boundary. The host cannot protect their
directory, synchronization service, backup, clipboard observer, or later copies. Keep exports and
clipboard data in approved private storage and follow the operator retention process.

Apply through migration 006 and configure the installation business time zone with
`ClaimCore.Database` before opening an existing installation with Web-v2.
See [Database](database.md) for migration and retention administration and
[Security and operations](operations.md) for deployment limits.
