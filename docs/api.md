# Service and CLI contract

Contracts owns one catalogue and fingerprint for fourteen endpoints. Session is GET; all
others are POST to the literal /api/<endpoint> path. Requests carry the matching contract
fingerprint. Missing or different fingerprints are refused before dispatch. CLI discovery needs no credentials. Session bootstrap is anonymous; the other service
operations require an authenticated, actor-bound caller.

| Endpoint | Responsibility |
| --- | --- |
| session | Authentication state, fingerprint and antiforgery token |
| session.logout | End the browser session |
| definition | Command/field presentation and runtime context |
| command.preview | Advisory business review |
| command.execute | Execute one exact business request |
| case.steward | Execute VOID, REINSTATE or ERASE |
| case.get | Current actor-visible record |
| case.list | Actor-visible bounded page |
| case.history | Summary or retained full history page |
| operation.observe | Accepted operation evidence visible to the actor |
| authority.register | Register a principal |
| authority.setGrant | Set one scoped role grant |
| authority.setEnabled | Enable or disable an actor |
| authority.observe | Observe an accepted authority event |

Schemas close objects and tags. Strict codecs reject duplicate members, invalid UTF-8,
invalid UUID/revision values and excess nesting/bytes. Authoring text is bound once through
Domain validation, so a business refusal retains its field diagnostic. Accepted response
values remain constrained. Generated artifacts are untracked and checked against the lock.

<a id="cc-cli-001"></a>
### CC-CLI-001 — Authenticated frames and exact delivery

The CLI reads {"endpoint":...,"input":...} frames over stdin and calls HTTPS with PKCE
or client credentials. It receives no store or database credential. One-line input is one
frame; multiple lines are NDJSON. Frame limit is128KiB. Bodies are strict UTF-8 and responses
are validated against the generated contract. Preview is advisory; execution revalidates.
Case execution outcomes are ACCEPTED, ALREADY_ACCEPTED, REJECTED, FAILED, CANCELLED and
OUTCOME_UNKNOWN. Authority outcomes use APPLIED and ALREADY_APPLIED for accepted events.

<a id="cc-cli-003"></a>
### CC-CLI-003 — Mutation uncertainty after dispatch

Exit0 is success;2 is protocol/business refusal;3 is definite local/service failure;
4 is dispatched mutation with uncertain delivery;130 is cancellation. Invocation/root
failures use64/70. An uncertain retry retains operation ID, expected revision and exact
request bytes. NDJSON stops on4 or130; it cannot let a later result mask uncertainty.
Host failures carry status, outcome and dispatch phase. CONTRACT_MISMATCH is NOT_STARTED;
reload before authoring a different request. No pending request is stored by the service.
