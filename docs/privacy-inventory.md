# Privacy inventory

The [actor route table](../src/ClaimCore.Server/ApiRoutes.fs), [attachment transport](../src/ClaimCore.Server/AttachmentRoutes.fs)
and [CSV transport](../src/ClaimCore.Server/ExportRoutes.fs) admit authenticated requests. The
[application disclosure path](../src/ClaimCore.Application/Reads/DisclosureReads.fs) commits an
event after the authorization snapshot closes. The independent recovery witness acknowledges the
committed observation before release. An event attests to an observation or release attempt.
Only the disclosure coordinator can construct a released reply. Actor routes require that type for
query results and unavailable-store fallbacks. Binary routes require it for content. Server export
preparation receives scoped queries and passes its candidate bytes through a fresh release check.

| Surface | Protected subjects and authorization cut | Audit evidence |
| --- | --- | --- |
| Case detail | Selected case and its embedded records at the read snapshot | Requester and authorized direct Case ID under Case read |
| Notes, parties, assignees, payments and attachment descriptors | Selected case and the rows returned by the subresource in the read snapshot | Requester, subresource kind, authorized Case ID and returned row count |
| Case list, search, summary and work views | Scoped rows or aggregates at the read snapshot | Returned row count and individually disclosed Case IDs. Aggregates omit contributor IDs |
| Reminder detail and list | Scoped case work in the read snapshot | Requester and read kind. Detail records the authorized Case ID. Lists record their row count and disclosed parent Case IDs |
| Related cases | Source and readable live target in the same snapshot. A source handler may receive an opaque removal handle for an active inaccessible target. | Evidence of the containing case read |
| Case timeline and revision conflicts | Selected case, current history capability, and current withdrawal or erasure in the response observation | Distinct Case timeline read and Case changes since review kinds. Authorized Case ID and any returned history page row count |
| Attachment download and PDF or image preview | Prepare verified bytes, then check the current descriptor and requester again in a fresh snapshot | Authorized case and attachment identifiers |
| Operation preview, execute, replay and lookup | An action commits independently of its receipt. Execution and replay acquire a fresh receipt snapshot after settlement. | Operation receipt or history observation |
| People directory, grants, history and request inbox | Scoped people or installation observations in the read snapshot | Returned row count and disclosed account IDs. Access requests omit principal identifiers |
| CSV download and page delivery | Prepared ordered row evidence and contributor membership checked again in a fresh snapshot | Query, safe selection shape and returned data row count |
| Disclosure trail | Installation-wide audit capability in the read snapshot. Bounds are fixed at traversal start. | Requester and trail-read kind |
| Daily digest | Enabled opted-in recipient and counts of currently readable assigned reminders are checked again after the dispatch claim. See [daily reminder digest](operating.md#daily-reminder-digest). | Recipient account ID and dispatch evidence. The address and reminder counts stay out of the trail. The relay receives the approved candidate after acknowledgement. |
| Protocol negotiation, liveness, readiness and sign-in bootstrap | Body-free admission and public operational status. Sign-in bootstrap and access-request submission have separate trust boundaries. | Host diagnostics and operational records |
| Requester context | Registered requester facts from the status snapshot | Requester and context kind |
| Session termination | Account identity and authorized cut at settlement. Browser termination is recoverable by its original request ID. | Requester and termination kind |
| Request dismissal, operator setup, restore and housekeeping | Configuration, authority or settlement through the owning command | Operational evidence and diagnostics |

Denied selectors omit target identities. The retention policy and erasure boundaries are described
in [Operating](operating.md#disclosure-trail). The live relationship dependency includes both case
IDs. Erasure clears the dependency, the target and note, and the associated private compartment.
The retained proof comprises the accepting operation and effect, original compartment owner and
commitment, cleared relationship marker, and the erasure position with its covered count. It
does not establish the erased topology. Independent text and externally held copies keep their own
lifetimes.

Web gives private endpoint displays a finite [authorization lifetime](web-ui.md#lifetime-of-private-views).
Screenshots, captured text and completed downloads are outside browser masking.
