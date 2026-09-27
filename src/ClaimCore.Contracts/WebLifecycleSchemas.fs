namespace ClaimCore.Contracts

open ClaimCore.Application
open ClaimCore.Domain

module internal WebLifecycleSchemas =
    let private reference = Schema.reference

    let private caseReference =
        FieldDefinitions.scalar "caseReference" |> ScalarSchemas.scalar

    let private reason = Schema.string None None (Some 1) (Some 500)

    let private actions =
        [
            WireSchema.kind "VOID_DATA_ENTRY_ERROR" [ WireSchema.property "reason" reason ]
            WireSchema.kind "REINSTATE_VOIDED" [ WireSchema.property "reason" reason ]
            WireSchema.kind "REQUEST_ERASURE" [ WireSchema.property "reason" reason ]
            WireSchema.kind "MARK_ERASURE_PENDING" [ WireSchema.property "reason" reason ]
            WireSchema.kind
                "RECORD_HOLD"
                [
                    WireSchema.property "holdId" WireSchema.uuid
                    WireSchema.property "ground" reason
                    WireSchema.property "reviewOn" WireSchema.date
                ]
            WireSchema.kind
                "RELEASE_HOLD"
                [
                    WireSchema.property "holdId" WireSchema.uuid
                    WireSchema.property "reason" reason
                ]
        ]

    let private changeProperties action =
        [
            WireSchema.property "eventId" WireSchema.uuid
            WireSchema.property "caseReference" caseReference
            WireSchema.property "expectedRevision" WireSchema.revision
            WireSchema.property "expectedLifecycleSequence" WireSchema.revision
            WireSchema.property "expectedLifecycleHash" WireSchema.digest
            WireSchema.property "action" action
        ]

    let reviewInput =
        WireSchema.objectOf [ WireSchema.property "caseReference" caseReference ]

    let applyInput = WireSchema.objectOf (changeProperties (Schema.oneOf actions))

    let approveInput =
        WireSchema.objectOf (
            changeProperties (
                Schema.oneOf (
                    actions
                    @ [
                        WireSchema.kind
                            "PURGE_PAYLOAD"
                            [
                                WireSchema.property "reason" reason
                                WireSchema.property "validUntil" WireSchema.timestamp
                            ]
                    ]
                )
            )
            @ [
                WireSchema.property "approvalId" WireSchema.uuid
                WireSchema.property "expiresAt" WireSchema.timestamp
            ]
        )

    let private outcome tag data =
        WireSchema.objectOf
            [
                WireSchema.property "tag" (WireSchema.token tag)
                WireSchema.property "data" data
            ]

    let private envelope endpoint cases =
        WireSchema.objectOf
            [
                WireSchema.property "endpoint" (WireSchema.token endpoint)
                WireSchema.property "outcome" (Schema.oneOf cases)
            ]

    let private reviewValue =
        WireSchema.objectOf
            [
                WireSchema.property "businessRevision" WireSchema.revision
                WireSchema.property "lifecycleSequence" WireSchema.revision
                WireSchema.property "lifecycleHash" WireSchema.digest
                WireSchema.property
                    "disposition"
                    (WireSchema.enumeration [ "ACTIVE"; "VOIDED_DATA_ENTRY_ERROR" ])
                WireSchema.property
                    "privacyPhase"
                    (WireSchema.enumeration
                        [
                            "ACTIVE"
                            "ERASURE_REQUESTED"
                            "ERASURE_PENDING"
                            "PAYLOAD_ERASED_SUPPRESSION_RETAINED"
                            "ERASURE_FINAL"
                        ])
                WireSchema.property
                    "activeHolds"
                    (WireSchema.array (
                        WireSchema.objectOf
                            [
                                WireSchema.property "holdId" WireSchema.uuid
                                WireSchema.property "reviewOn" WireSchema.date
                            ]
                    ))
                WireSchema.property "voidRequiresTwoApprovals" Schema.boolean
            ]

    let reviewResponse =
        envelope
            "lifecycle.review"
            [
                outcome "AVAILABLE" reviewValue
                outcome "RESOURCE_UNAVAILABLE" Schema.nullValue
                outcome "CANCELLED" Schema.nullValue
                outcome "FAILED" (reference "Fault")
            ]

    let refusal =
        WireSchema.enumeration
            [
                "INVALID_IDENTITY"
                "INVALID_REASON"
                "INVALID_TIME"
                "WRONG_CASE"
                "VERSION_CONFLICT"
                "REVISION_EXHAUSTED"
                "WRONG_DISPOSITION"
                "ERASURE_HAS_BEGUN"
                "WRONG_PRIVACY_PHASE"
                "DUPLICATE_HOLD"
                "HOLD_NOT_FOUND"
                "HOLD_ACTIVE"
                "HOLD_CAPACITY_EXCEEDED"
                "APPROVAL_REQUIRED"
                "APPROVAL_CAPACITY_EXCEEDED"
                "APPROVAL_MISMATCH"
                "APPROVAL_EXPIRED"
                "ERASURE_EVIDENCE_INCOMPLETE"
            ]

    let writeResponse endpoint =
        envelope
            endpoint
            [
                outcome
                    "APPLIED"
                    (WireSchema.objectOf
                        [
                            WireSchema.property "eventId" WireSchema.uuid
                            WireSchema.property "businessRevision" WireSchema.revision
                            WireSchema.property "lifecycleSequence" WireSchema.revision
                        ])
                outcome "REFUSED" (WireSchema.objectOf [ WireSchema.property "reason" refusal ])
                outcome "RESOURCE_UNAVAILABLE" Schema.nullValue
                outcome
                    "CANCELLED_BEFORE_ADMISSION"
                    (WireSchema.objectOf [ WireSchema.property "eventId" WireSchema.uuid ])
                outcome "FAILED" (reference "Fault")
                outcome
                    "UNCONFIRMED"
                    (WireSchema.objectOf [ WireSchema.property "eventId" WireSchema.uuid ])
            ]
