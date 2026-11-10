# Workspace examples

These screens use fictional data and the published service's Core HUMAN documents. Language and
regional formats are independent. The payment journey records 55 entries, then removes one recorded
in error: the light workspace shows 55, while the dark and Arabic views show the resulting 54.

| Example | What it shows |
| --- | --- |
| [English light workspace](images/foundations/case-workspace-light.png) | Facts, recorded stages, payment totals and the work rail |
| [English dark workspace](images/foundations/case-workspace-dark.png) | The same workspace after the reviewed entry removal |
| [Arabic narrow payment cards](images/foundations/payment-cards-ar.png) | RTL reading order and complete money values at 320 CSS pixels |
| [Paged payments](images/foundations/paged-payments.png) | Current payment entries supplied through the authorized continuation |
| [Assignee selection](images/foundations/assignee-selection.png) | Supplied account captions with a separate responsibility change |
| [Correction groups](images/foundations/correction-groups.png) | Core current facts and selected registration/decision changes |
| [Correction review](images/foundations/correction-review.png) | Before/after presentation and one confirmation in the review drawer |
| [Authority receipt](images/foundations/authority-receipt.png) | The recorded account and ordered effects, with technical metadata collapsed |

The captures come from the existing [payment](../web/e2e/payments.spec.ts),
[case-work](../web/e2e/case-work.spec.ts), [correction](../web/e2e/localization-correction.spec.ts) and
[authority](../web/e2e/stewardship-authority.spec.ts) journeys. Screenshots complement the executable
checks; human native-language and assistive-technology review remain unverified.

Earlier [redesign images](images/redesign/) and [localization images](images/localization/) remain
historical design references. See [Case work](case-work.md) and [People and access](people-and-access.md)
for supported interactions.
