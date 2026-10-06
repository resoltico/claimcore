# ClaimCore Web

`ClaimCore.Web` is the authenticated HTTPS case-work service and browser host. It alone composes the actor-bound core over the primary PostgreSQL store and independent witness; the React browser and CLI are clients, not database peers. The host binds one configured HTTPS origin and an explicit listener address/port; native defaults remain loopback. Its OIDC identities and ClaimCore grants provide individual authority, but a same-machine installation is not evidence of separate-host custody or a qualified internet-facing deployment. Private-file startup is supported on macOS and Linux; unsupported host file security fails closed.

## Web executable

The generated block below is synchronized with the compiled host. Discovery does not load private
configuration or open PostgreSQL.

<!-- generated:begin web-help -->
```text
ClaimCore.Web 0.7.0 — HTTPS human interface
  ClaimCore.Web                 Start the configured HTTPS host
  ClaimCore.Web help            Show this configuration-free help
  ClaimCore.Web version         Show the compiled product version
  ClaimCore.Web version --json  Show compiled release identity as JSON
  ClaimCore.Web probe live     Probe this configured listener without opening a runtime
  ClaimCore.Web probe ready    Probe independently qualified real-data readiness
Required settings (credentials are private file paths):
  CLAIMCORE_CONNECTION_FILE
  CLAIMCORE_WITNESS_CONNECTION_FILE
  CLAIMCORE_WITNESS_KEY_FILE
  CLAIMCORE_WRITER_CAPABILITY_FILE
  CLAIMCORE_SUPPRESSION_KEY_FILE
  CLAIMCORE_RECOVERY_ARTIFACT_KEY_FILE
  CLAIMCORE_WEB_CERTIFICATE_PATH
  CLAIMCORE_WEB_STATE_DIR
  CLAIMCORE_OIDC_ISSUER
  CLAIMCORE_OIDC_CLIENT_ID
  CLAIMCORE_OIDC_CLIENT_SECRET_FILE
  CLAIMCORE_OIDC_API_AUDIENCE
  CLAIMCORE_OIDC_CLI_CLIENT_ID
  CLAIMCORE_OIDC_SERVICE_CLIENT_ID
Optional settings:
  CLAIMCORE_WEB_ORIGIN
  CLAIMCORE_WEB_LISTEN_ADDRESS
  CLAIMCORE_WEB_LISTEN_PORT
  CLAIMCORE_WEB_PROBE_CA_CERT_FILE
  CLAIMCORE_FULL_AUDIT_INTERVAL_SECONDS
  CLAIMCORE_WEB_MAX_JSON_BYTES
  CLAIMCORE_WEB_CORE_PERMITS
  CLAIMCORE_WEB_CORE_QUEUE
  CLAIMCORE_WEB_LOGIN_PERMITS
  CLAIMCORE_WEB_SESSION_IDLE_MINUTES
  CLAIMCORE_WEB_SESSION_ABSOLUTE_MINUTES
  CLAIMCORE_OIDC_CA_CERT_FILE
Conditional real-data evidence settings:
  CLAIMCORE_BACKUP_HEALTH_POLICY_FILE
  CLAIMCORE_BACKUP_HEALTH_CERTIFICATE_FILE
```
<!-- generated:end web-help -->

## Publish and start

[Service operation](service.md) owns Docker operation, persistent local deployment, mounts and process supervision.

[Getting started](getting-started.md) owns the complete source-to-first-login sequence. Build Web
assets once with the locked Node 26 frontend, then publish `ClaimCore.Web`. Publication verifies the
asset manifest against exact source, npm lock, generated Web-v3 contract, Node/npm toolchain, notices,
and output bytes before placing assets under `wwwroot`. Ordinary .NET builds never invoke npm.

Start the published assembly only after provisioning separate primary and witness credentials, owner-private witness and suppression keys, an independent raw writer-capability file, a recovery-artifact key ring, an OIDC issuer/client, and the HTTPS certificate and state directory. Supply their paths through the variables below; do not place credential bytes in command arguments or tracked files. The certificate must be an owner-private blank-password PKCS#12/PFX with a private key for the configured public HTTPS identity. Startup metadata discovery uses one joining separator for root and path issuers and a ten-second
whole-request deadline through the bounded body read. Browser login challenges the configured OIDC issuer with Authorization Code and S256 PKCE; there is no shared bootstrap credential. Restart revokes the host's server-side browser sessions.

Startup refuses a certificate outside its validity period, without a matching DNS/IP subject
alternative name, or without the server-authentication extended key usage. A matching self-signed
certificate still requires the operator to verify and trust its public certificate in the browser.

## Configuration reference

| Variable | Requirement and bound |
|---|---|
| `CLAIMCORE_CONNECTION_FILE` | Required owner-private primary application-role connection file. |
| `CLAIMCORE_WITNESS_CONNECTION_FILE` | Required owner-private, separately credentialed witness-writer connection file. |
| `CLAIMCORE_WITNESS_KEY_FILE` | Required owner-private witness key ring; never sent to browser or CLI. |
| `CLAIMCORE_WRITER_CAPABILITY_FILE` | Required absolute owner-private regular file with exactly 32 random, nonzero raw bytes and no linked path components; mode `0600` on macOS/Linux. The witness binds its hash to one active writer generation. Never pass the bytes in arguments or expose them to browser/CLI. |
| `CLAIMCORE_SUPPRESSION_KEY_FILE` | Required owner-private keyed-commitment material for erased-reference and operation suppression. |
| `CLAIMCORE_RECOVERY_ARTIFACT_KEY_FILE` | Required owner-private recovery-artifact encryption/key-policy file. |
| `CLAIMCORE_WEB_CERTIFICATE_PATH` | Required absolute path to a regular, non-linked blank-password PFX with a private key, at most 8 MiB; owner-only on POSIX. |
| `CLAIMCORE_WEB_STATE_DIR` | Required absolute owner-private directory; the host creates it as mode `0700` on POSIX and rejects a linked/reparse directory. |
| `CLAIMCORE_OIDC_ISSUER`, `CLAIMCORE_OIDC_CLIENT_ID`, `CLAIMCORE_OIDC_CLIENT_SECRET_FILE` | Required issuer URL, browser BFF client ID, and owner-private confidential-client secret file. Real-data issuer discovery and token endpoints require validated HTTPS. |
| `CLAIMCORE_OIDC_API_AUDIENCE`, `CLAIMCORE_OIDC_CLI_CLIENT_ID`, `CLAIMCORE_OIDC_SERVICE_CLIENT_ID` | Required API audience and distinct public-CLI/service-client identities for bearer admission. |
| `CLAIMCORE_OIDC_CA_CERT_FILE` | Optional private test CA for a loopback or reserved `.localhost` synthetic issuer; remote issuers use system trust. |
| `CLAIMCORE_WEB_ORIGIN` | Optional exact public HTTPS DNS/IP origin with no path, user information, query, or fragment; defaults to `https://localhost:5443`. |
| `CLAIMCORE_WEB_LISTEN_ADDRESS` | Optional literal IPv4/IPv6 socket address; defaults to `127.0.0.1`. Docker explicitly uses `0.0.0.0`. |
| `CLAIMCORE_WEB_LISTEN_PORT` | Integer 1–65,535; defaults to the public origin port. |
| `CLAIMCORE_WEB_PROBE_CA_CERT_FILE` | Optional owner-private public CA for probes of a loopback or reserved `.localhost` origin; remote probes use system trust. |
| `CLAIMCORE_FULL_AUDIT_INTERVAL_SECONDS` | Hosting audit cadence; see [runtime admission](architecture.md#cc-run-001). |
| `CLAIMCORE_BACKUP_HEALTH_POLICY_FILE`, `CLAIMCORE_BACKUP_HEALTH_CERTIFICATE_FILE` | Conditional owner-private real-data qualification inputs; see [Operations](operations.md). |
| `CLAIMCORE_WEB_SESSION_IDLE_MINUTES` | Integer 1–30; defaults to 30. |
| `CLAIMCORE_WEB_SESSION_ABSOLUTE_MINUTES` | Integer 1–480, not less than idle lifetime; defaults to 480. |
| `CLAIMCORE_WEB_MAX_JSON_BYTES` | Integer 1–65,536; defaults to 65,536. |
| `CLAIMCORE_WEB_CORE_PERMITS` | Integer 1–4; defaults to 4. |
| `CLAIMCORE_WEB_CORE_QUEUE` | Integer 1–16; defaults to 16. |
| `CLAIMCORE_WEB_LOGIN_PERMITS` | Integer 1–5 per fixed one-minute window; defaults to 5. |

The native listener defaults to loopback. Explicit interface binding supports Docker forwarding and remote operation while exact Host, TLS, origin, identity and grant admission remain mandatory. It sets a
131,072-byte Kestrel ceiling solely for the largest raw envelope import; every endpoint applies its
own lower limit before allocation. `GET /health/live` is an HTTPS host liveness probe, not an authenticated readiness, database-integrity, installation-readiness, or backup check. `GET /health/ready` deliberately returns `503` until separate-host backup, witness, and restore qualification is implemented and verified.

## Browser presentation

The browser offers English, Latvian and Arabic interface text, with right-to-left layout for Arabic.
Language and display format are separate controls: the available formats are `en-GB`, `lv-LV` and
`ar-EG`. The defaults are English and `en-GB`, regardless of the host or browser locale. Expanded
English (`en-XA`) is a layout-test pseudolocale, not a translation for case work.

Changing either preference does not submit a command or clear a draft, prepared review, consent or
recovery identity. Preferences are stored locally in the browser when storage is available; they do
not change the installation's business time zone or recorded currency. Displayed dates and accepted
amounts follow the selected format, but authored dates and amounts retain their canonical input
syntax. Copied canonical values and downloaded recovery artifacts remain exact.

The case detail reads disposition and privacy status through the authorized lifecycle review. It
shows active holds as a separate erasure blocker and warns when a data-entry-error void requires
two distinct approvals; voiding does not reverse a payment. An unavailable review exposes no
lifecycle details. Lifecycle mutations and approvals remain explicit actor-scoped service actions,
not implicit browser buttons or ordinary case commands.

## Web-v3 contract and admission

<a id="cc-web-001"></a>
### CC-WEB-001 — Exact HTTPS authority, Web-v3 admission, routes, and typed outcomes

The pure `ClaimCore.Contracts` projection owns the canonical Web-v3 endpoint catalog, exact request
and response schemas, raw-media rules, deterministic response codecs, split TypeScript DTO modules,
parsed conformance corpus, and Web wire fingerprint. The F# host consumes the same catalog and codec
authority. A deterministic Node postprocess compiles the aggregate response schema into strict AJV
standalone shared host, discovery, core, and recovery validator groups and a typed asynchronous selector. The selector loads
only the group needed to validate the endpoint response. Run `npm --prefix web run contract:check` to
regenerate and compare every checked artifact; do not hand edit generated contract files.
During the pre-1.0 source preview, `/api/v3` identifies this route and admission family, not a
promise that every response shape remains backward-compatible. The host and bundled browser must
agree on the exact Web fingerprint before claimant-bearing responses are accepted.
Signer, copy-deletion, copy-adoption, writer-handoff, and real-data-activation approval instants, plus an erasure-purge proposal's `validUntil`, use UTC microsecond precision: the required seven fractional digits end in `0`. Sub-microsecond input is refused before it can create signed evidence that PostgreSQL cannot round-trip exactly.

Before the host admits requests, shared HostSecurity checks physical credential, certificate, state, and lock paths through opened handles. Existing broad-mode, extended-ACL, or linked files are refused without chmod or redirected creation. OIDC client secrets, witness keys, and suppression material remain server-side.
Private-file startup fails closed on Windows rather than guessing an equivalent ACL check.
Configuration-free `help` and `version` remain available before private admission; unsupported
process arguments fail with a usage refusal instead of starting the host.

Only `/api/v3` is an API surface. `/api/v1`, `/api/v2`, and every other unknown `/api/*` request return a typed,
no-store `404` and never fall through to the single-page application. Business and Application
outcomes are HTTP `200` endpoint bodies. Typed host failures use the appropriate `400`, `401`, `403`,
`404`, `409`, `413`, `415`, `429`, `500`, or `503` response and state
`executionPhase: NOT_STARTED` or `STARTED_UNCONFIRMED` where meaningful.

Identity selection uses exact case-sensitive claim names and requires exactly one authenticated identity and one subject; bearer identity also requires exactly one authorized client claim. Duplicate identity claims are refused even when their values agree. Discovery rejects nonobject and duplicate-member documents and compares the complete issuer URI; canonical root issuers and slash-ended HTTPS endpoints are supported.

Browser sessions remain server-side behind secure, HTTP-only, same-site cookies and are bounded by idle and absolute expiry. Access and refresh tokens are not exposed to browser JavaScript. A fresh OIDC login does not revive an expired session.

The ticket store checks elapsed idle and absolute limits under its gate, as well as the original
bounded UTC deadline. A backward wall-clock adjustment cannot extend elapsed session life, and
renewal cannot replace the original absolute limit. A shorter original ticket expiry also bounds
elapsed life; a forward UTC change may conservatively refuse a session. Expired lookup keys are
removed and cannot be revived by renewal or a later clock correction.

| Method and path | Service behavior |
|---|---|
| `GET /auth/login` | Start the OIDC Authorization Code/S256 PKCE browser challenge. |
| `GET /api/v3/session` | Anonymous or current browser session snapshot and antiforgery token. |
| `POST /api/v3/session/logout` | Revoke the browser cookie and return an anonymous snapshot. |
| `GET /api/v3/definition` | Authorized definition with semantic and Web fingerprints. |
| `POST /api/v3/cases/get`, `/list`, `/history` | Actor-scoped case reads and pagination. |
| `POST /api/v3/operations/observe`, `/prepare`, `/submit` | Actor-scoped operation observation and command preparation/execution. |
| `POST /api/v3/recovery/list`, `/inspect`, `/resolve`, `/dismiss`, `/export` | Actor-scoped recovery; export binds the exact operation ID and request digest. |
| `POST /api/v3/recovery/import-envelope/preview`, `/retain` | Preview or retain the exact signed recovery artifact bytes. |
| `POST /api/v3/authority/register`, `/grants/set`, `/actors/enable`, `/observe`, `/copy-signers/approve`, `/copies/deletion/approve`, `/copies/adoption/approve`, `/writer-handoffs/approve`, `/real-data-activation/review`, `/real-data-activation/approve` | Registered actor/grant and exact copy-signer, deletion, adoption, writer-handoff, or real-data activation review and approval subject to ClaimCore roles; schema-owner custody and execution remain separate. |
| `POST /api/v3/lifecycle/review`, `/apply`, `/approve` | Witnessed disposition, erasure, hold, and approval workflow. Owner-only purge is not an HTTP action. |
| `POST /api/v3/tombstones/review`, `/prune/approve`, `/terminal/approve`, `/holds/change` | Opaque post-purge steward review, prune or terminal-evidence draft approvals, and holds. Owner-only pruning and terminal certification are not HTTP actions. |

Case-list pages use opaque continuations bound to the authenticated principal, current grant
revision, page request, and a short expiry. A cursor from another user, a changed grant or page
request, an expired cursor, or a host restart must start a fresh listing; raw case references are
not accepted as continuations. Hidden cases are filtered before the visible page is formed.

After live-case purge, authorized data stewards use the opaque tombstone case ID to review the
independently sealed witness-payload target, record or release holds with closed nonclaimant codes,
and approve that exact target. The endpoint does not return a case reference, claimant details, or
an owner execution capability. Two distinct witnessed approvals do not by themselves complete
erasure; the owner-only prune and managed-copy verification still have separate gates. After a
verified witness prune, the same nonpayload review reports that witness ciphertext was pruned while
managed-copy certification remains pending; it does not expose erased bytes or claim final erasure.

Every route first requires the exact configured Host and the configured listener peer policy. Explicit interface binding permits Docker/remote peers; TLS, credential-mode, origin, authentication and grant checks remain mandatory. Static assets, liveness, and the session snapshot do not require an authenticated browser session. Browser mutations require a current OIDC session, exact origin/fetch metadata, antiforgery token, and the endpoint's exact media/body bound. Bearer automation and CLI calls require a validated issuer, audience, token, client identity, and ClaimCore actor grants; a browser cookie and bearer token cannot be mixed. Actor/resource authorization occurs before disclosure and again under mutation authority locks. Missing and inaccessible case/operation identities have the same public refusal. Invalid UTF-8, scalars, tokens, grants, or oversized bodies fail with safe no-store host outcomes; admitted endpoint outcomes use the Contracts codec.

Recovery import is raw `application/vnd.claimcore.recovery+json`, at most 131,072 bytes. Retain resends the exact previewed bytes with the generated source-digest header; the service rechecks current actor grant, case privacy, artifact/export identity, and witnessed authority before releasing retained request material. Raw canonical-record import is not supported.

The browser validates every JSON result against its exact endpoint schema, validates non-success
statuses against the closed host-failure contract, and checks the expected Web fingerprint before
accepting claimant-bearing data. Cross-endpoint and malformed values fail closed. The browser neither
reconstructs a transition nor gains access to a store, retained preparation, recovery port, or
transition callback.

An exact prepare retry after acceptance returns typed `OBSERVED_ACCEPTED` with the authoritative receipt to a currently authorized actor, not technical preparation details or a new advisory review. It remains available after the
accepted preparation is pruned. Exact retained material that is no longer reviewable returns
`RETAINED_FOR_RECOVERY` with a typed reason. Neither branch auto-submits; the operator follows the
exact recovery identity when needed.

An exact submit or resolve identity also observes the accepted receipt after pruning without a new
attempt; pruned technical material cannot be exported.

## Session and delivery safety

Core admission permits four active requests and sixteen queued requests by default, with oldest-first
queueing; configured limits may tighten these bounds. Overflow returns typed HTTP 429 WEB_BUSY with
executionPhase NOT_STARTED before core dispatch. This refuses this request, without settling an older
uncertain operation. Rebuild host and clients with the matching generated contract after this wire
refinement; old null-phase busy responses no longer match the current schema.

An editor keeps navigation and logout unavailable until its explicit Back or accepted Return to case
action. Language and format changes preserve the draft and review. Return uses the accepted receipt's
reference, including OPEN, then rereads current state and available commands. Session snapshots are
applied in request order; a late older refresh cannot replace newer session knowledge or undo logout.
Every newly established session epoch starts a fresh authenticated client subtree.

Logout is unavailable while a mutation is dispatched. When it succeeds, the host signs out the server-side browser ticket and cookie, makes the principal anonymous, issues a fresh anonymous antiforgery token, and returns that session snapshot. The browser increments its local session epoch, aborts reads, and clears claimant state.

A definite server access refusal on a paginated read clears that request's cached rows, continuation
and page metadata. Transient delivery/storage failures remain retryable; other reads keep their own
state. Already-disclosed downloads, clipboard contents and human knowledge are not remotely erased.

Reads may be abortable. Mutation HTTP never uses browser cancellation after dispatch. A typed
not-started admission response proves no execution. Browser JSON and export response waits have a
twenty-second deadline, including body delivery and validation; expiration neither aborts a mutation
nor proves that the server failed. A later response cannot replace the already-reported uncertainty.
A lost prepare response remains uncertain and may be retried or inspected with the exact operation ID and request bytes. A lost, timed-out,
malformed, wrong-media, or undecodable submit response after dispatch is
`STARTED_UNCONFIRMED`: preserve only operation ID, digest, and recovery direction, clear claimant
state, and never retry automatically.

An uncertain submit or non-reviewable retained preparation opens Recovery with that exact identity.
Inspect remains explicit and does not depend on the operation appearing in the pending list.
Operations lookup is prefilled with the same ID for observation when preparation is absent or pruned;
absence is never proof of failure. A known accepted receipt remains acceptance evidence when its
response also reports unconfirmed witness settlement; the browser separately directs Recovery.

## Recovery, downloads, and clipboard

The browser lists actionable pending recovery work by default and offers an explicit bounded terminal
view for accepted and revoked technical material. Detailed inspection pages identified attempts with
an opaque operation-bound cursor; unset settlements remain uncertain even after a later accepted retry. A
pruned revoked preparation is a payload-free tombstone, not a substitute for claimant data. The UI
can resolve or dismiss only after accessible confirmation using the exact operation ID and digest.
An accepted receipt is not offered as a new resolve action; an exact replay can observe it without a
second accepted revision. Reload clears browser review state; durable recovery is the Application
workflow, not browser state.

Recovery resolve, dismiss, retain and export lock navigation/logout through dispatch. Resolution and dismissal
invalidate old inspected mutation actions; results retain the subject ID/digest for fresh inspection.
Reads and import previews can be canceled; late observation, inspection or attempt-page responses
cannot overwrite a changed target or reopen a closed dialog. An explicit core RECOVER_EXACT direction
remains uncertain inside generic faults and failed-before-attempt responses.

Export first warns the operator, then accepts only a bounded attachment named exactly
`claimcore-recovery-<canonical-operation-id>.json` with media type
`application/vnd.claimcore.recovery+json`; duplicate, mismatched UTF-8, suffix-spoofed, or inline
content dispositions fail closed. The file contains claimant data. Import uses file
selection, preview, confirmation, and retain of identical bytes; it never auto-submits.

Downloads and clipboard content leave ClaimCore's process boundary. The host cannot protect their
directory, synchronization service, backup, clipboard observer, or later copies. Keep exports and
clipboard data in approved private storage and follow the operator retention process.

Initialize a separate fresh database and explicit installation business calendar with
`ClaimCore.Database initialize <canonical-IANA-ID>` before opening Web-v3. Old installations and
old recovery artifacts are refused untouched, not upgraded.
See [Database](database.md) for initialization, verification and retention administration and
[Security and operations](operations.md) for deployment limits.

## Diagnostic and delivery boundaries

Host failure payloads require a stable diagnostic, exact safe parameters, and `status` matching the
actual HTTP response. Status and execution phase come from typed causes, never message text.
Pre-dispatch errors, unconfirmed dispatch, and failed response delivery remain distinguishable;
only established pre-dispatch refusal claims no operation started. Partial responses are aborted,
not followed by a second JSON object. Startup failures use bounded structured stderr without raw
provider or configuration values. [Product diagnostics](diagnostics.md) owns this boundary and its
native/wire break. Rebuild browser and host from matching generated contracts.

### Startup resource ownership

Failed configuration closes acquired issuer trust. Startup owns its server and issuer certificates
before metadata verification and owns the built application through route setup and execution.
Application services close before runtime and certificate owners; startup refusal does not leave a
verified pool, certificate or unstarted host behind. Trust validation and private-file rules remain
the same on supported hosts.
