# CLI and protocol

`ClaimCore.Cli` is ClaimCore's strict JSON CLI-v3 automation interface. `ClaimCore.Web` is the
human interface; `ClaimCore.Database` is the separate schema-owner administration program. The CLI
is local process automation, not a remote API, and it has no labelled-text, legacy-verb, or
protocol-v2 compatibility mode.
Private-file runtime endpoints are supported on macOS and Linux; Windows discovery commands remain
available, but private-file operations fail closed there.

After a Release build, use the executable directly:

```text
dotnet run --project src/ClaimCore.Cli --configuration Release --no-build -- help
dotnet run --project src/ClaimCore.Cli --configuration Release --no-build -- describe summary
```

Discovery commands do not open PostgreSQL. Runtime endpoints read the Npgsql connection string from
the owner-private file selected by `CLAIMCORE_CONNECTION_FILE`.

## Commands

This block is synchronized with the compiled CLI. Synchronization establishes identity, not semantic
correctness; process and contract tests provide separate behavioral evidence.

<!-- generated:begin cli-help -->
```text
ClaimCore CLI v3
  help [topic]
  version
  version --json
  describe summary
  describe fields [field-name]
  describe commands [command-kind]
  describe endpoints [endpoint-id]
  describe recovery
  schema invocation|response|definition|recovery-envelope
  schema endpoint <endpoint-id>
  call
  session

Discovery commands do not open PostgreSQL. call reads one strict JSON invocation from stdin; session reads NDJSON.
```
<!-- generated:end cli-help -->

`call` reads exactly one invocation from standard input and emits exactly one JSON response.
`session` reads one newline-delimited JSON invocation per line and emits one response per accepted
input line while retaining a retryable runtime session. It does not accept positional command verbs,
paths containing claimant data, aliases, text output, or protocol selection flags.

## Contracts and invocation

The pure `ClaimCore.Contracts` projection owns the semantic description, CLI-v3 catalog, invocation
schema, exact aggregate and per-endpoint request/response schemas, recovery-envelope schema, pure
response codec, conformance corpora, and CLI wire fingerprint. Use `describe` to discover semantic
fields, commands, endpoints, and recovery methods; use `schema` to obtain exact machine-readable
schemas. Checked artifacts in the Web workspace are projections of the same source, not another
authority.
During the pre-1.0 source preview, `protocolVersion: 3` identifies this CLI framing and endpoint
family, not a promise that every response shape remains backward-compatible. The exact wire
fingerprint identifies the current schema; automation must use the matching contract rather than
assuming the number alone establishes compatibility.

Every invocation is an exact JSON object with `protocolVersion: 3`, an endpoint ID, and that
endpoint's `input`. Only descriptors marked cancellable accept optional `timeoutMs`; mutation
endpoints do not accept a browser-style cancellation timeout. Duplicate or unknown properties,
malformed UTF-8, unpaired escaped Unicode, comments, trailing documents, wrong scalar/container
kinds, invalid UUIDs, and noncanonical digests or revisions are protocol failures. Failure paths
identify registered structural locations; unknown authored property names are never echoed.

For example, the first command in the synthetic walkthrough is an invocation, not an old request
file:

```json
{
  "protocolVersion": 3,
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
        "insurerName": "Example Alleged Insurer",
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

CLI-v3 is distinct from durable canonical command-record format 2 and recovery-envelope format 1.
The latter formats preserve exact recovery bytes; they are not earlier CLI protocols.

## Endpoint inventory

| Endpoint ID | Core operation |
|---|---|
| `command.prepare`, `command.execute` | `Prepare`, `Execute` |
| `case.get`, `case.list`, `case.history`, `operation.observe` | Typed case and receipt queries |
| `recovery.list`, `recovery.inspect`, `recovery.resolve`, `recovery.dismiss` | `IClaimsCore.Recovery` lifecycle work |
| `recovery.export` | Export to an absolute owner-private destination through the CLI private-file service |
| `recovery.importEnvelopePreview`, `recovery.importEnvelopeRetain` | Preview or retain an envelope from an absolute private source path |
| `recovery.importRecordPreview`, `recovery.importRecordRetain` | Preview or retain a canonical command record from an absolute private source path |

`command.execute` is the one-call typed command workflow. `recovery.resolve` reuses only an already
retained operation ID and digest; it does not recreate or edit a draft. Import preview never submits,
and retain never auto-submits.

## Responses and exit codes

Every ordinary response is one JSON object. A completed invocation uses:

```json
{"protocolVersion":3,"kind":"result","endpoint":"case.list","outcome":{"kind":"succeeded","items":[],"nextCursor":null}}
```

An intake or framing failure uses `kind: "protocolFailure"` with a stable code, safe message, and
JSON pointer path. Endpoint outcomes remain typed body data: business rejection, not found, failure,
cancellation, definite execution, settlement confirmation, and recovery refusal are never inferred
solely from an exit code.

If one-call `command.execute` cannot establish whether preparation retention committed, it returns
`preparationStateUnknown` with the exact operation ID and request digest and exits 4; it never
relabels that uncertainty as a pre-attempt failure. A `recovery.resolve` cancelled before storage
admission returns `cancelledBeforeAdmission` with the operation ID and exits 130, rather than claiming
that the preparation was absent.

An exact `command.prepare` retry after acceptance returns `observedAccepted` with the authoritative
receipt (exit 0), not technical preparation details or a fabricated current-state review. It remains
available after the accepted preparation is pruned. If an exact retained request is no longer
reviewable against current state, it returns
`retainedForRecovery` with the retained details and typed reason (exit 2); inspect or resolve the
same identity rather than auto-submitting a stale review.

| Exit | Meaning |
|---:|---|
| 0 | Successful endpoint result, including prepared or observed-accepted work. |
| 2 | Protocol failure, business rejection, conflict, not found, or refused recovery action. |
| 3 | Configuration, schema, storage, or definite non-mutating failure. |
| 4 | Attempt, commit, retention, or result-delivery outcome remains uncertain. |
| 64 | Unsupported CLI invocation. |
| 70 | Unexpected pre-admission process failure. |
| 130 | Cancellation completed before mutation admission or attempt. |

On a broken output stream after mutation admission, stderr reports only the operation ID, optional
digest, and safe recovery direction. It never reports case payloads, recovery bytes, paths,
credentials, or connection strings.

## Canonical request identity and recovery

Canonical format-2 request identity preserves authored string content. Thus amount spellings such as
`"1"` and `"1.00"` identify different request content even though accepted views render the same
decimal canonically. Case references retain exact Unicode content and casing.

For an uncertain result, preserve the exact operation ID and original request bytes. First use
`operation.observe`; then inspect recovery as necessary. Never create a replacement operation ID,
edit the request, rebuild it from rendered output, silently refresh its revision, or infer failure
from a momentarily absent receipt.

<a id="cc-rec-001"></a>
### CC-REC-001 — Recovery preserves exact, installation-bound request identity

Recovery is one Application-owned workflow. A preparation is technical material, not accepted claim
history, and its existence does not establish commit. `recovery.inspect` returns retained effect and
provenance separately from its receipt observation. It also shows actual identified attempts and
their definite settlements, and the pre-003 uncertainty marker independently of provenance. A
bounded recovery list deliberately omits authored values, provenance, and attempt detail.
An accepted receipt remains observable even if its technical preparation has been pruned. While a
preparation is retained, exact replay preserves its original producer provenance and timestamp; a
newer binary's semantic fingerprint does not rewrite those first-writer facts or become part of
operation identity. An exact `recovery.resolve` identity can observe the accepted receipt after
pruning, but cannot export a preparation that no longer exists.
Concurrent identical retains classify one creator and subsequent exact replays as existing without
replacing the first retained bytes.

Resolve, dismiss, and export require the exact operation ID and SHA-256 digest. A started or unknown
attempt is not a license to regenerate a request. A mismatched identity refuses a mutation without
returning the other preparation's metadata or authored values. `recovery.inspect` remains an
intentional trusted-operator read by operation ID. Cancellation proved before a technical COMMIT does
not imply that write committed and does not erase earlier attempt or receipt evidence. A lost result
once COMMIT starts remains explicitly uncertain. A definite business execution remains visible even
if its technical settlement cannot be confirmed; unknown and unresolved outcomes remain recoverable.

<a id="cc-cli-002"></a>
### CC-CLI-002 — Private CLI recovery artifacts stay inside handle-first paths

`recovery.export` writes only through the handle-first private-file service to an absolute destination
with exclusive creation, owner-private mode without extended ACLs, flush, and post-write validation.
It will not follow a leaf or ancestor link or overwrite an existing file; a failed write removes only
the partial file this call created. Imports require an absolute, owner-private regular source without
link traversal; handle identity checks also reject a pathname replaced during inspection. Imports
reject invalid UTF-8 and oversized bytes before decoding. Refusal responses and
diagnostics do not echo the private path, artifact bytes, credentials, or claimant data. An envelope
is installation-bound and contains claimant data. Preview hashes exact import-source bytes; retain
requires the same digest and never submits. Envelope and canonical-record imports have separate
preview and retain endpoints, limits, and artifact semantics. On Windows these private-file endpoints
refuse before access until equivalent handle and ACL verification is implemented.
