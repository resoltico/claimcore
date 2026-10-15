# Browser and HTTPS host

Web authenticates OIDC authorization-code PKCE sessions and bearer clients, binds their
principal to the actor-bound core, and renders typed outcomes. Domain/Application own decisions.
The browser calls the generated contract using cookie credentials, antiforgery and its
fingerprint. It has no database credentials or durable operation store.

<a id="cc-web-001"></a>

### CC-WEB-001 — HTTPS admission and typed outcomes

Web requires the configured HTTPS origin, admitted OIDC issuer/audience and matching contract.
Cookie-authenticated POST requests require antiforgery, including reads; bearer delivery is
separately authenticated. Method, media type, duplicate/extra members, UTF-8, depth 32 and a
configurable body limit up to 64 KiB are checked before the core factory. API requests do not
follow redirects. Failed dispatch cannot invent a Domain
outcome. Durable execution continues despite an abandoned HTTP caller; loss of confirmation
is uncertain. Request logs contain only endpoint, outcome, status, durationMs, actorId and
operationId. Exception diagnostics contain type and method stack, never exception messages.

The editor freezes one draft identity and serialized request for review/execution. Editing
an exposed request creates a fresh identity. Unknown delivery retains one pending request for
retries of the same bytes while the page is alive; a closed/reloaded page retains no pending
operation. Contract mismatch prompts reload and does not replay a draft. Display safety is distinct
from business acceptance; authored Unicode is preserved and suspicious code points can be shown as warnings.

Current views distinguish live, voided and erased records. Erased views expose only reference,
revision and state; history exposes metadata without snapshots. Command advertisements are
advice. The service locks and revalidates execution. The browser offers English, Latvian and
Arabic with independent date/number formats and right-to-left Arabic layout. Latvian and Arabic
catalogues are machine-drafted and require native-language review. Pseudolocalization supports
layout checks. Stewardship and authority management use the API or CLI.

Bearer tokens require an admitted issuer/audience and `JWT` or `at+jwt` type. The CLI client is
human and requires `sub`. Other verified clients identify services by client ID; matching single
`azp`/`client_id` values or their single-claim fallback are accepted. Duplicate, blank or conflicting
client identities are refused. IdP verification does not register or grant a ClaimCore actor.

Configure SERVICE actors only for confidential clients restricted to client credentials; disable
interactive authorization and other user grant paths for those clients. Configure the CLI as a
public human authorization-code/PKCE client with service accounts disabled. Issue the API audience
only in access tokens, never ID tokens. Classification by `azp`/`client_id` identifies the client;
it does not prove which OAuth grant produced a token. These IdP flow restrictions are part of the
deployment trust contract and prevent user tokens from inheriting a shared service actor’s grants.

The browser consumes tracked contract types/descriptors and validates strict bounded JSON plus
required endpoint/outcome shapes. Missing or unusable mutation receipt data remains uncertain
and preserves the original retry identity/content. WITHHELD snapshots show
metadata without fields; ERASED is distinct. Country display hints use localized names where known,
accept syntactically valid unassigned codes, and explain `ZZ` without imposing membership policy.
