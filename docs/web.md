# Browser and HTTPS host

Sign in through OIDC, then use the views offered by your current ClaimCore grants. Authentication alone grants
no case work. The browser uses same-origin secure cookies and antiforgery; it holds no database credentials.

Choose a case to read its grouped registration, decision and payment facts. Available acts come from
current core advice. Use the authority view to manage actors and grants when your rights permit it.
The [case guide](domain.md) explains corrections; the [CLI reference](cli.md) offers the same acts outside
the browser.

## Review and record a change

Forms take fields, prefill paths and tagged branches from the generated catalog, localized text from the
generated message contract, and
permitted work from core advice. Review shows a proposal; confirmation sends a frozen request with its exact
operation ID and expected revision. Execution rechecks current authority and state.

An uncertain submit offers **Send again** and **Check outcome**. Send again uses the exact frozen bytes.
Check outcome calls operation.resolve for final acceptance or non-acceptance. Ordinary faults leave uncertainty
visible. On reload, only Check outcome remains: sessionStorage holds `{operationId, scope, act}`, never the
body or business values. Storage failures leave the current-page flow working. After NOT_ACCEPTED, read current
state before authoring a new operation.

Access refusal or session replacement clears disclosed views. Results from an old actor/target cannot reveal
data in the new context. Authority refusal invalidates context, offers and actor/grant lists together.
Revalidation failures wait for explicit reload; page cursor errors reset their local page. Selected-actor grant
pages carry that response's target header. Prepared authority requests survive read-view remounts.

Current, voided and erased records remain distinct, as do DISCLOSED, WITHHELD and ERASED receipt snapshots.
Erasure review explains copies and backups outside live erasure. Observation finds accepted metadata but cannot
establish that uncertain submitted content matched it.

## Language, display and navigation

English, Latvian and Arabic use the same source catalogs and ICU checks. Latvian/Arabic text still needs native
review. RTL layout, display-format preferences, country hints and suspicious-Unicode warnings preserve raw
business strings. Navigation focuses destination headings; background reads leave focus in place. Read failures
offer an explicit reload, and views default-deny before context succeeds. Human assistive-technology review
remains separate from automated checks.

<a id="cc-web-001"></a>

### CC-WEB-001 — HTTPS admission and typed outcomes

Web requires a configured HTTPS origin, OIDC issuer/audience and matching contract identity. Cookie POSTs
require antiforgery; bearer authentication is separate. Method, media type, closed shape, UTF-8, nesting and
body bounds are checked before core creation. Clients refuse redirects. Abandoning an HTTP request does not
cancel admitted durable execution; lost confirmation remains uncertain.

Bearer tokens require admitted issuer/audience and JWT/at+jwt type. Human CLI tokens require sub; other
clients identify SERVICE actors through admitted, nonconflicting azp/client_id. Duplicate, blank or conflicting
client claims refuse. Operators must enforce the [provider grant-flow restrictions](operations.md#access-review-and-sessions).

CLAIMCORE_WEB_KEY_RING_DIRECTORY names an existing protected absolute directory. Keys and database-backed
protected tickets survive restart under the [session policy](operations.md#access-review-and-sessions).
Safe request logs contain endpoint/outcome/status/duration and technical actor/operation IDs. Exception logs
exclude messages, credentials and claimant data.

Readiness admits at most two concurrent probes; saturation returns 503 without database work. Each admitted
probe links request abort, shutdown and a two-second cancellation deadline through provider cleanup. Its permit
remains held until cleanup finishes; the deadline is not a hard response-time guarantee. Liveness remains
independent. Readiness establishes installation/schema access, without proving freshness or case outcomes.

See [Web composition](../src/ClaimCore.Web/Composition/) for dispatch and [browser source](../web/README.md)
for the transport/form modules. [API](api.md) owns byte admission, correlation and delivery semantics.
