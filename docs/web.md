# Browser and HTTPS host

Web owns authentication, technical admission and composition; Domain/Application own decisions. The browser is a
cookie/antiforgery HTTPS client with no database credentials or local payload store.

<a id="cc-web-001"></a>

### CC-WEB-001 — HTTPS admission and typed outcomes

Web requires configured HTTPS origin, OIDC issuer/audience and matching contract identity. Cookie POSTs require
antiforgery; bearer delivery authenticates separately. Method, media type, closed shape, UTF-8, depth 32 and
configurable body limit up to 64 KiB are checked before core creation. Clients refuse redirects. Abandoning an HTTP
request does not cancel durable execution; lost confirmation remains uncertain. Safe request logs contain endpoint,
outcome, status, durationMs, actorId and operationId; exceptions log type/method stack without messages or claimant
data. `CLAIMCORE_WEB_KEY_RING_DIRECTORY` names an existing protected absolute directory. Data-protection keys and
encrypted server-side tickets survive restarts; [Operations](operations.md#access-review-and-sessions) owns deadlines,
protection, logout and qualified shared-clock coordination. Bearer tokens require admitted issuer/audience and
JWT/at+jwt type. The human CLI requires sub; other clients identify SERVICE actors by the admitted nonconflicting
azp/client_id. Duplicate, blank or conflicting client claims refuse. IdP authentication grants no ClaimCore authority.
[Provider flow restrictions](operations.md#access-review-and-sessions) remain operator obligations.

One explicit operation-state engine and thin effects hook serve CASE and AUTHORITY through preview, consent and execute.
Controls, paths, tagged branches, diagnostic focus and CLI operands use checked interaction bindings;
labels/policy/order come from typed descriptors, and permitted work from current core advice.
Controls retain stable authored names and associate core meanings, hints and field refusals programmatically. Diagnostic
focus follows explicit bindings, including the nested reference in case-scoped grants. Recovery rows use authored
scope-specific action labels while retry identities retain protocol values.
Preview never grants commit permission. Authority preview applies the same authorization and last-owner constraints as
execute. The prepared request freezes exact bytes, operation ID and expected revision. Editing an exposed request creates a fresh identity and
clears consent. An uncertain request blocks editing/rebasing; retry uses exact bytes. Later failures do not clear
uncertainty. Only correlated exact acceptance or replay resolves it. Explicit warned abandonment is distinct from
proving failed execution. Page memory retains frozen bytes; reload lists only the actor's authorized protected
submission metadata. Explicit opaque retry may accept previously unaccepted work and requires consent; listing/login
never executes. User acknowledgement of a known result may forget its row; failed cleanup never changes acceptance.

Access refusal/session replacement clears disclosed server views independently from pending bytes. Completion
from an old actor/target cannot reveal data in the new context. Authority refusal invalidates context, offers
and actor/grant lists together; revalidation failures wait for explicit reload. Pending authority operations
survive read remounts. Grant requests load only the selected actor, render that page's target header, and
clear/abort on actor/token/disclosure changes. Cursor errors reset only their local page; only explicit
access/session refusal implies wider loss. Fresh authoring requires successful offers; advice retry preserves
prepared requests.

Strict response decoding validates bounded UTF-8 bytes and generated complete-response checks before correlation.
Malformed or mismatched mutation replies preserve uncertainty. Current/voided/erased views and DISCLOSED/WITHHELD/ERASED
receipt snapshots stay distinct. Erasure review repeats live-database limits; WAL, backups and copies remain outside
ERASE. Observation cannot confirm pending content. English/Latvian/Arabic catalogs share source JSON and ICU checks;
Latvian/Arabic need native review. RTL, display-format preferences, pseudolocalization, country hints and
suspicious-Unicode warnings never rewrite business strings. Explicit navigation focuses destination headings; background
reads do not move focus. Read failures offer in-page retry and views default-deny before context succeeds.
Transport/operation modules remain independent from views/presentation; the service facade owns networking and
transitive module checks reject bypasses/platform dependencies in pure protocol code.

Readiness permits two concurrent probes without queuing; saturation returns 503 without database work while
liveness remains independent. Each probe links request abort, shutdown and a two-second cancellation deadline
through runtime/Postgres cleanup. The permit remains held until cleanup ends; two seconds is a cancellation
deadline, not a hard response-time guarantee. Readiness establishes installation/schema access, never accepted
case outcomes, freshness or owner approval.
