# Browser and HTTPS host

Web authenticates OIDC authorization-code PKCE sessions and bearer clients, binds their
principal to IActorClaimsCore, and renders typed outcomes. Domain/Application own decisions.
The browser calls the generated contract using cookie credentials, antiforgery and its
fingerprint. It has no database credentials or durable operation store.

<a id="cc-web-001"></a>
### CC-WEB-001 — HTTPS admission and typed outcomes

Web requires the configured HTTPS origin, admitted OIDC issuer/audience and matching contract.
Cookie mutations require antiforgery; bearer delivery is separately authenticated. Method,
media type, duplicate/extra members, UTF-8, depth32 and body16MiB guards reject before the
core factory. API requests do not follow redirects. Failed dispatch cannot invent a Domain
outcome. Durable execution continues despite an abandoned HTTP caller; loss of confirmation
is uncertain. Request logs contain only endpoint, outcome, status, durationMs, actorId and
operationId. Exception diagnostics contain type and method stack, never exception messages.

The editor freezes one draft identity and serialized request for review/execution. Editing
an exposed request creates a fresh identity. Unknown delivery permits one retry of the same
bytes while the page is alive; a closed/reloaded page retains no pending operation. Contract
mismatch prompts reload and does not replay a draft. Display safety is distinct from business
acceptance; authored Unicode is preserved and suspicious code points can be shown as warnings.

Current views distinguish live, voided and erased records. Erased views expose only reference,
revision and state; history exposes metadata without snapshots. Command advertisements are
advice. The service locks and revalidates execution. English, Latvian, Arabic and a seeded
pseudolocale are exercised by tests; Latvian/Arabic text still needs native-language review.
