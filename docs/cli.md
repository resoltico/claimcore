# CLI and protocol

Service JSON responses have a 16 MiB buffered allowance, separate from the 128 KiB request-frame and
recovery-file limits. Full history pages repeat valid business fields and may exceed a single record's
allowance, particularly after Unicode JSON escaping. Response buffering still uses the request deadline.

`ClaimCore.Cli` is ClaimCore's strict JSON CLI-v4 client of the authenticated HTTPS service. `ClaimCore.Web` hosts case work and browser sessions; `ClaimCore.Database` is a separately credentialed owner administration program. The CLI has no PostgreSQL driver, runtime database credential, or direct store path. Old CLI-v3 frames and direct-database case work are not compatibility modes. Private source, destination, and automation-secret files use handle-first admission; unsupported host file security fails closed.

After a Release build, use the executable directly:

```text
dotnet run --project src/ClaimCore.Cli --configuration Release --no-build -- help
dotnet run --project src/ClaimCore.Cli --configuration Release --no-build -- describe summary
```

Discovery commands require no service or credentials. Case-work calls require `CLAIMCORE_SERVICE_URL`, `CLAIMCORE_OIDC_ISSUER`, `CLAIMCORE_OIDC_CLIENT_ID`, and `CLAIMCORE_CLI_AUTH_MODE` (`interactive` or `automation`). Automation also requires an owner-private `CLAIMCORE_OIDC_CLIENT_SECRET_FILE` for its distinct client-credentials principal. Interactive login uses an authorization-code/S256 PKCE loopback callback. The service and issuer URLs must be HTTPS; system certificate trust is the default. The optional private `CLAIMCORE_CLI_OIDC_TRUST_ROOT_FILE` and `CLAIMCORE_CLI_SERVICE_TRUST_ROOT_FILE` are restricted to loopback qualification and do not disable hostname validation. No bearer token or client secret belongs in a frame, log, or repository file. System-trust HTTPS checks certificate revocation during new handshakes. Loopback private roots must be current signing CAs with public-only material; the server leaf must carry server-authentication purpose.

A supplied public-root file that fails handle-first admission returns
`CLI_PRIVATE_SOURCE_INVALID` before authentication. Check that it exists at a physical
absolute path, is a regular file owned by the current user in mode `0600`, and has no
linked component or extended ACL on the file or any ancestor. Its directory must be
owner-private; broad ancestors must satisfy the root-owned/sticky-parent rules. A
malformed or unsuitable public root returns `CLI_CONFIGURATION_INVALID`. Paths and
provider details are never reflected. Use the [local operator workflow](service.md#persistent-local-evaluation)
for an ACL-bearing macOS home; do not strip ACLs or change existing broad directories.

## Commands

This block is synchronized with the compiled CLI. Synchronization establishes identity, not semantic
correctness; process and contract tests provide separate behavioral evidence.

<!-- generated:begin cli-help -->
```text
ClaimCore CLI v4
  help [topic]
  version
  version --json
  describe summary
  describe fields [field-name]
  describe commands [command-kind]
  describe endpoints [endpoint-id]
  describe recovery
  describe diagnostics
  schema invocation|response|definition|recovery-envelope
  schema endpoint <endpoint-id>
  call
  session

Discovery commands do not connect to the service. call reads one strict JSON invocation from stdin; session reads NDJSON over authenticated HTTPS.
```
<!-- generated:end cli-help -->

`call` reads exactly one invocation from standard input and emits exactly one JSON response.
`session` reads one newline-delimited JSON invocation per line and emits one response per accepted
input line while retaining an authenticated client session. It does not accept positional command verbs,
paths containing claimant data, aliases, text output, or protocol selection flags.

Each invocation is limited to 131,072 bytes (excluding the session newline). An oversized
`session` frame emits `FRAME_TOO_LARGE` and terminates with exit 2 immediately after the first
excess byte, without waiting for a newline or EOF. Restart the session before sending more work;
the unread tail is discarded when the process closes its input. Complete malformed frames still
produce frame-local refusals and permit the next line. Earlier completed responses retain their
own outcomes; this refusal does not settle any earlier uncertain operation.

## Local failures and core outcomes

Configuration, authentication, invalid service replies, and private-file refusals return a CLI-v4 `localFailure` with a safe code and exit 3. A service host refusal is a distinct `serviceFailure`; a service outcome is nested under `result.service` and validated against the generated endpoint response schema before delivery. An uncertain outbound mutation or delivery returns a typed unconfirmed result and exit 4. Neither transport failure nor a temporarily absent receipt proves that a mutation failed. [Core outcome diagnostics](diagnostics.md) defines the service's safe diagnostic identities.

Protocol failures carry flat `diagnosticId`, `code`, and a known-member `path` in the
`protocolFailure` frame, without a core-style `diagnostic` object or parameters. Use
`claimcore describe diagnostics` for protocol and process schemas. A process delivery failure uses
structured stderr, preserves only the current frame's known operation context, and never writes a
second stdout frame. A potentially state-changing frame with unconfirmed delivery exits 4; no
failure diagnostic authorizes implicit retry. Export metadata mismatch is a local integrity failure,
not a manufactured core rejection. See [Product diagnostics](diagnostics.md).

## Contracts and invocation

The pure `ClaimCore.Contracts` projection owns the semantic description, CLI-v4 invocation/response schemas, the generated Web-v3 service endpoint catalog and response schemas, recovery-artifact schema, conformance corpora, and wire fingerprints. Use `describe` to discover fields, commands, endpoints, and recovery methods; use `schema` for exact machine-readable shapes. Checked browser and CLI artifacts are projections of this source, not separate authorities. `protocolVersion: 4` identifies the CLI framing, not a compatibility guarantee. The exact wire
fingerprint identifies the current schema; automation must use the matching contract rather than
assuming the number alone establishes compatibility.

Every invocation is an exact JSON object with `protocolVersion: 4`, an endpoint ID, and that
endpoint's `input`. Only descriptors marked cancellable accept optional `timeoutMs`; mutation
endpoints do not accept a browser-style cancellation timeout. Duplicate or unknown properties,
malformed UTF-8, unpaired escaped Unicode, comments, trailing documents, wrong scalar/container
kinds, invalid UUIDs, and noncanonical digests or revisions are protocol failures. Failure paths
identify registered structural locations; unknown authored property names are never echoed.

For example, the first command in the synthetic walkthrough is an invocation, not an old request
file:

```json
{
  "protocolVersion": 4,
  "endpoint": "command.execute",
  "input": {
    "operationId": "10000000-0000-4000-8000-000000000001",
    "caseReference": "DEMO-0001",
    "expectedRevision": "0",
    "command": {
      "kind": "OPEN",
      "values": {
        "incidentDate": "2026-08-01",
        "incidentNotificationDate": "2026-08-03",
        "incidentCountry": "Lithuania",
        "claimantName": "Example Translation Services Ltd",
        "insurerName": "Example Responsible Insurer",
        "claimedAmount": "1000.00",
        "claimedCurrency": "EUR"
      }
    }
  }
}
```

`values` is an exact, unordered object on the wire. Application verifies its complete key set and
orders the authored values by Domain command definition before canonical encoding. The case reference
remains the immutable top-level target, not a command value.

CLI-v4 is distinct from durable canonical command-record format 3 and signed recovery-artifact format 3.
The latter formats preserve exact recovery bytes; they are not earlier CLI protocols.

## Endpoint inventory

| Endpoint ID | Service operation |
|---|---|
| `command.prepare`, `command.execute` | Actor-bound preparation and execution |
| `case.get`, `case.list`, `case.history`, `operation.observe` | Authorized case and operation reads |
| `recovery.list`, `recovery.inspect`, `recovery.resolve`, `recovery.dismiss` | Authorized recovery work |
| `recovery.export` | Obtain an authorized envelope and create an exclusive owner-private local file |
| `recovery.importEnvelopePreview`, `recovery.importEnvelopeRetain` | Read a private local envelope and preview or retain its exact bytes through the service |
| `authority.register`, `authority.setGrant`, `authority.setEnabled`, `authority.observe`, `authority.approveCopySigner`, `authority.approveCopyDeletion`, `authority.approveCopyAdoption`, `authority.approveWriterHandoff` | Installation/case authority and exact copy-signer, deletion, adoption, or writer-handoff approval subject to service grants; owner custody and execution are separate |
| `lifecycle.review`, `lifecycle.apply`, `lifecycle.approve` | Audited disposition, erasure, hold, and approval workflow subject to service grants |
| `tombstone.review`, `tombstone.approvePrune`, `tombstone.approveTerminal`, `tombstone.changeHold` | Opaque post-purge review, witnessed prune or terminal-evidence draft approval, and nonpayload holds; owner certification and execution are separate |

For copy-signer, copy-deletion, copy-adoption, writer-handoff, and real-data-activation approvals, and the `validUntil` of an erasure-purge proposal, supply UTC instants with seven fractional digits ending in `0`; finer precision is refused because the signed value must survive PostgreSQL's microsecond timestamp storage unchanged.

The generated service catalog defines exact paths, body media, and response schemas; the CLI never sends a database connection string. Every case-work endpoint checks the authenticated principal and current ClaimCore grants before disclosure or mutation. `command.execute` is the one-call typed command workflow. `recovery.resolve` reuses only an already
retained operation ID and digest; it does not recreate or edit a draft. Import preview never submits,
and retain never auto-submits.

`case.list` continuations are opaque and bound to the authenticated principal, current grant
revision, exact page size, and a 15-minute lifetime. A changed grant, different page size, expired
token, or service restart requires a fresh first page. Do not save a cursor as a case reference or
assume it is portable between sessions or installations.

## Responses and exit codes

Every ordinary response is one JSON object. A successful case-list response has the CLI envelope around the validated service outcome:

```json
{"protocolVersion":4,"kind":"result","endpoint":"case.list","service":{"endpoint":"case.list","outcome":{"tag":"SUCCEEDED","data":{"items":[],"nextCursor":null}}}}
```

An intake or framing failure uses `kind: "protocolFailure"` with a stable code, safe diagnostic identity, and structural JSON-pointer path. Authentication/configuration and private-file refusals use `localFailure`; an admitted HTTP host refusal uses `serviceFailure`. Endpoint outcomes remain typed service data: business rejection, not found, cancellation, witnessed settlement, and recovery refusal are not inferred solely from an exit code.

If one-call `command.execute` cannot establish whether preparation or attempt admission committed, its service outcome preserves the exact operation ID and request digest and the CLI exits 4. A timeout, lost response, or other unconfirmed delivery after dispatch must be reconciled through the same identity; the client cannot infer that a service mutation failed. Cancellation proved before admission exits 130.

An exact prepare or execute retry after acceptance may return `OBSERVED_ACCEPTED` with the authoritative receipt (exit 0), not a fabricated current-state review. A retained request no longer reviewable against current state remains `RETAINED_FOR_RECOVERY` (exit 2); inspect or resolve that same identity rather than auto-submitting a stale review. A current grant or erasure fence can still deny disclosure of an older receipt.

| Exit | Meaning |
|---:|---|
| 0 | Successful endpoint result, including prepared, accepted, and observed-accepted work. |
| 2 | Protocol failure, business rejection, conflict, not found, or refused recovery action without `RECOVER_EXACT` guidance. |
| 3 | Configuration/authentication, private-file, invalid service-reply, or definite service failure. |
| 4 | Mutation, settlement, or result delivery remains uncertain. |
| 64 | Unsupported CLI invocation. |
| 70 | Unexpected pre-admission process failure. |
| 130 | Cancellation completed before mutation admission or attempt. |

On a broken output stream after mutation dispatch, stderr carries only a bounded safe process diagnostic and known frame identity/recovery direction. It never reports case payloads, recovery bytes, private paths, tokens, or credentials.

<a id="cc-cli-003"></a>
### CC-CLI-003 — Remote mutation delivery remains uncertain after dispatch

Every generated mutating CLI endpoint is noncancellable after admission. A timeout, malformed service reply, lost HTTP response, or failed stdout delivery after dispatch exits 4 when the mutation may have started; it is not evidence that the service did not commit. Preserve the exact approval, event, or operation identity and inspect the relevant service or owner evidence before retrying. Read-only endpoints retain definite failure classification because they cannot create authority or change case data. Recovery export issues witnessed custody evidence and is therefore noncancellable. An explicit core fault recommending RECOVER_EXACT remains uncertain even when wrapped in FAILED. A verified export whose private destination refuses creation reports RESULT_OBSERVED; it never claims that service issuance did not start.

SIGINT terminates the process even while stdin or stdout is blocked. Framing seals future dispatch
before choosing the signal exit code: no possible mutation in the current frame gives 130; possible
mutation dispatch or incomplete mutation output gives 4. A signal may leave no complete response
frame, so preserve the submitted identity and inspect exact service evidence for exit 4. Earlier
completed session frames keep their own outcomes. Process exit closes process-owned streams and
connections; it does not prove that a dispatched server operation stopped or rolled back.

Discovery, token acquisition and service response reads each carry the client's existing twenty-second
whole-request deadline through headers and bounded content. A timeout after mutation dispatch retains
unconfirmed delivery; it never means that server execution rolled back.

Token validity and reuse are bounded by elapsed time from token HTTPS dispatch and UTC expiry.
Reuse retains the thirty-second margin; a fresh shorter-lived token can be used once but is not
cached for reuse. Body delivery does not start a new lifetime, and backward wall-clock adjustments
cannot extend elapsed validity. CLI refuses a reply delivered after the actual lifetime, before
service dispatch. Credential expiry does not settle an earlier uncertain operation.

Core RECOVER_EXACT direction also exits 4 inside a fault or recovery refusal, including dismissal
refused because submission already started and a FAILED_BEFORE_ATTEMPT response. That response can
describe an earlier uncertain operation; refusing a new attempt does not settle the earlier one. Preserve its exact identity and inspect before retry.

## Canonical request identity and recovery

Canonical format-3 request identity preserves authored string content. Thus amount spellings such as
`"1"` and `"1.00"` identify different request content even though accepted views render the same
decimal canonically. Case references retain exact Unicode content and casing.

Current recovery import accepts only the signed, installation-bound recovery artifact v3 carrying canonical command format 3. Raw canonical-record import and older unsigned/plaintext envelopes are not supported. No compatibility rewrite or relabeling is performed. Snapshot format 2 and SHA-256 fingerprint algorithm version 1 are separate internal format identities; an import neither adopts an old database nor submits a command.

For an uncertain result, preserve the exact operation ID and original request bytes. First use
`operation.observe`; then inspect recovery as necessary. Never create a replacement operation ID,
edit the request, rebuild it from rendered output, silently refresh its revision, or infer failure
from a momentarily absent receipt.

<a id="cc-rec-001"></a>
### CC-REC-001 — Recovery preserves exact, installation-bound request identity

Recovery is one Application-owned workflow. A preparation is technical material, not accepted claim
history, and its existence does not establish commit. `recovery.inspect` returns retained effect and
provenance separately from its receipt observation. It pages actual identified attempts and their
definite settlements with an opaque cursor bound to that operation. Unsettled identified attempts
remain independent of later acceptance and prevent pruning of their preparation. `recovery.list` defaults to pending work and takes an explicit
terminal view for retained accepted/revoked evidence and payload-free revocation tombstones. A bounded
recovery list deliberately omits authored values, provenance, and attempt detail.
An accepted receipt remains observable to a currently authorized actor even if its technical preparation has been pruned. While a
preparation is retained, exact replay preserves its original producer provenance and timestamp; a
newer binary's semantic fingerprint does not rewrite those first-writer facts or become part of
operation identity. An exact `recovery.resolve` identity can observe the accepted receipt after
pruning, but cannot export a preparation that no longer exists.
Concurrent identical retains classify one creator and subsequent exact replays as existing without
replacing the first retained bytes.

Resolve, dismiss, and export require the exact operation ID and SHA-256 digest. A started or unknown
attempt is not a license to regenerate a request. A mismatched identity refuses a mutation without
returning the other preparation's metadata or authored values. `recovery.inspect` is actor- and resource-scoped; an inaccessible operation must not become an existence oracle. Cancellation proved before a technical COMMIT does
not imply that write committed and does not erase earlier attempt or receipt evidence. A lost result
once COMMIT starts remains explicitly uncertain. A definite business execution remains visible even
if its technical settlement cannot be confirmed; unknown and unresolved outcomes remain recoverable.
A durable revocation is different from a rejected attempt: it ends future execution authority for the
exact identity, remains terminal after optional preparation pruning, and never changes earlier
attempt knowledge. Attempt admission is capped at 64 per operation; reaching the cap does not block
inspect, dismiss, export, or accepted replay.

<a id="cc-cli-002"></a>
### CC-CLI-002 — Private CLI recovery artifacts stay inside handle-first paths

After the service authorizes an export, `recovery.export` writes only through the handle-first private-file service to an absolute destination
with well-formed Unicode, exclusive creation, owner-private mode without extended ACLs, flush, and post-write validation.
It will not follow a leaf or ancestor link or overwrite an existing file; a failed write removes only
the partial file this call created. Imports require an absolute, owner-private regular source without
link traversal; malformed UTF-16 path scalars refuse before native encoding can alias another filename.
Valid Unicode filenames are preserved. Failed lock validation closes its acquired descriptor before
returning a refusal; an inode whose identity could not be established is not deleted. Handle identity checks also reject a pathname replaced during inspection. Imports
reject invalid UTF-8 and oversized bytes before decoding. Refusal responses and
diagnostics do not echo the private path, artifact bytes, credentials, or claimant data. An envelope
is installation-bound and contains claimant data. Preview hashes exact import-source bytes; retain
requires the same digest and never submits. Only the signed current recovery-artifact envelope has
preview and retain endpoints. On Windows these private-file endpoints
refuse before access until equivalent handle and ACL verification is implemented.
