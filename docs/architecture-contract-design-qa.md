# Architecture and contract design QA

This separate pass challenges [the design](architecture-contract-design.md) before production changes. Source inspection and synthetic probes are design evidence, not owner approval.

| Challenge | Observation and required resolution |
| --- | --- |
| Moving decoders merely rearranges files. | Schemas, codecs and public DTOs currently have different owners; FuzzQualificationTests cannot directly reach Web-only decoders without importing the host. Moving pure codecs closes that boundary and enables direct totality qualification. Keep stream/Kestrel concerns out of Contracts. |
| Contracts would acquire host effects. | HttpInputSupport has one asynchronous bounded stream reader and one ASP.NET exception branch. Extract both into the Web HTTP body owner before moving the pure helpers. ArchUnitNET must continue proving Contracts avoids selected host IO and ambient APIs. |
| Internalising IClaimsCore blocks normal callers. | Runtime publicly supplies IActorClaimsCore; IClaimsCore is returned only by internal CoreApi. Change the ordinary consumer positive control to actor-bound APIs and prove the internal seam is inaccessible. Friends still compose it; no public store/factory appears. |
| Export is a safe query because it returns bytes. | Source issues a witnessed managed-copy export and runtime already uses mutation admission. Native probes report Cancellable=true and exit 3 for completed-response loss. Remove the read-only classification and exercise the actual client boundary. |
| FAILED always means definite failure. | A native RecoveryMutationUnknown wire result reports FAILED but carries RECOVER_EXACT and currently exits 3. Use the core-owned guidance to preserve uncertainty; controls must distinguish definite faults and rejected/cancelled outcomes. |
| Cancellation proves successful export never happened. | ExportRetained converts an Ok signed artifact into RecoveryCancelled if cancellation arrives afterwards. Successful issuance must retain its observed result; cancellation before admission remains definite. |
| Private destination failure happened before service work. | The failure is reached only after a verified HTTP export reply. RESULT_OBSERVED must be schema-supported and must not imply no issuance; invalid or lost replies stay unconfirmed. |
| A new architecture selector proves enforcement by existing alone. | Require nonempty owner/subject/exception selectors, real production relationships and a bad/good JSON decoder fixture. OIDC is an explicit third-party boundary; no wildcard codec exemption. |
| Removing duplicated rules weakens checks. | ProductTests.tests registers graph/unused-reference/test-dependency/composition checks only. Its copied private effect rules have no registration; OwnershipTests registers the same protections. Delete the unused copies and verify every existing test remains discovered. |
| Manifest parsing is harmless trusted data. | Silent duplicate/unknown fields can hide accidental policy edits. Expose the pure parser to isolated controls; exact schema fields, object arrays and repository-local project paths are required before any assembly or project loading. |
| Better architecture means more abstractions. | Reject a service bag, universal generator, plugin dispatcher and component splits without a concrete need. Keep explicit typed workflows, independent witnesses, owner-only administration and platform-native build inputs. |

Proceed with the reviewed changes, then regenerate affected contracts/inventories and run the required suites, published clients and full CI. Revisit the design if actual tool or runtime behavior contradicts these decisions.
