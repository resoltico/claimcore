# Browser and HTTPS host

Web authenticates OIDC authorization-code PKCE sessions and bearer clients, then supplies verified
subject facts to Hosting’s protocol service. Hosting binds the actor core and renders typed outcomes. Domain/Application own decisions.
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

`CLAIMCORE_WEB_KEY_RING_DIRECTORY` is required and must name an existing absolute directory
without group or other write permission. The host persists ASP.NET data-protection keys there
under application name `ClaimCore.Web`. Reuse this directory so protected cookie bytes and
antiforgery tokens remain decryptable; a different directory cannot decrypt them. Browser
session tickets still live in a process-local store, so a restart ends those sessions even
with the same keys. The key ring does not persist session tickets. Protect it as a secret;
its Linux XML files are unencrypted, and it is outside database backups.

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
layout checks. Authority form selects and non-checkbox inputs size to their panel while checkboxes
retain native dimensions. Stewardship and authority management are reachable browser operations, using core offers and generated forms.

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

The browser consumes tracked generated types/descriptors, request builders and complete response schemas.
Its production decoder starts with bounded strict UTF-8 bytes and preserves integer lexical checks. Missing or unusable mutation receipt data remains uncertain
and preserves the original retry identity/content. WITHHELD snapshots show
metadata without fields; ERASED is distinct. Country display hints use localized names where known,
accept syntactically valid unassigned codes, and explain `ZZ` without imposing membership policy.

Case stewardship uses the preview/review/execute flow. The confirmation dialog names the
prepared act, shows its exact authored reason and authoritative before/after record state, and repeats
the owned act meaning, including live-database erasure limits. These values remain separate from
the thirteen business fields. Authority actions use local exact-request
review and authoritative execute; there is no authority preview endpoint. Confirmation is a local
consent action, with human/service, last-owner and grant rules enforced by Application. ERASE removes
live database payload history and does not reach WAL, backups or copies. Display preferences and RTL
never change request bytes. Operation lookup remains independent from pending-content confirmation.

Interaction modules own local state, focus, consent, exact pending bytes and accumulated delivery
knowledge while the page remains alive. A retry failure cannot clear earlier uncertainty; correlated
exact acceptance/replay or content-exclusion evidence is needed. `src/api/transport.ts` owns networking behind the service facade;
interaction/transport do not import views. Access refusal and session replacement clear stale server
disclosures. Authority refusals invalidate context, offers and actor listings together, then independently
revalidate current read and management rights. A refusal during revalidation clears both caches and
waits for explicit reload; exact authored pending requests and uncertainty remain separate.
Case lists derive OPEN advice from their own returned page. Actor review loads grant pages only
for the selected actor and renders that page's current target header. Actor/token/disclosure changes
abort and clear selected reads without remounting pending authority operations. Unselected actors
cause no grant requests; partial pages expose load-more/end state and explicit fresh reload.
An invalid cursor clears only its local page/advice, without inferring global privilege loss.
Authority advice read failures remain visible with an explicit retry. Fresh authoring requires
a successful nonempty offer set; retrying advice does not discard or replace a prepared request.
Generated protocol purity follows the resolved transitive import closure, including JavaScript
modules, and rejects React, locale, clock, randomness and I/O dependencies. Generated TypeScript
is included in the normal type-aware lint stage.
