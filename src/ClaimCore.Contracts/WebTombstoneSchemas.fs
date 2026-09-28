namespace ClaimCore.Contracts

open ClaimCore.Domain

/// Opaque post-purge steward authority. There is no case reference or claimant-bearing reason
/// in these requests or outcomes; owner execution remains outside the public service.
module internal WebTombstoneSchemas =
    let private sequence = WireSchema.revision
    let private digest = WireSchema.digest

    let reviewInput =
        WireSchema.objectOf [ WireSchema.property "caseId" WireSchema.uuid ]

    let private proposal =
        WireSchema.objectOf
            [
                WireSchema.property "eventId" WireSchema.uuid
                WireSchema.property "caseId" WireSchema.uuid
                WireSchema.property "purgeEventId" WireSchema.uuid
                WireSchema.property "purgeWitnessSequence" sequence
                WireSchema.property "purgeWitnessEpoch" sequence
                WireSchema.property "purgeWitnessHash" digest
                WireSchema.property "cutoffSequence" sequence
                WireSchema.property "cutoffHash" digest
                WireSchema.property "targetCount" sequence
                WireSchema.property "targetDigest" digest
                WireSchema.property "expectedAuthorityRevision" sequence
                WireSchema.property "expectedAuthorityHash" digest
                WireSchema.property "validUntil" WireSchema.timestamp
            ]

    let approveInput =
        WireSchema.objectOf
            [
                WireSchema.property "proposal" proposal
                WireSchema.property "approvalId" WireSchema.uuid
                WireSchema.property "expiresAt" WireSchema.timestamp
            ]

    let private groundCode = WireSchema.enumeration TombstoneHoldPolicy.groundCodes

    let private releaseCode = WireSchema.enumeration TombstoneHoldPolicy.releaseCodes

    let holdInput =
        WireSchema.objectOf
            [
                WireSchema.property "eventId" WireSchema.uuid
                WireSchema.property "caseId" WireSchema.uuid
                WireSchema.property "expectedAuthorityRevision" sequence
                WireSchema.property "expectedAuthorityHash" digest
                WireSchema.property
                    "mutation"
                    (Schema.oneOf
                        [
                            WireSchema.kind
                                "RECORD"
                                [
                                    WireSchema.property "holdId" WireSchema.uuid
                                    WireSchema.property "groundCode" groundCode
                                    WireSchema.property "reviewOn" WireSchema.date
                                ]
                            WireSchema.kind
                                "RELEASE"
                                [
                                    WireSchema.property "holdId" WireSchema.uuid
                                    WireSchema.property "releaseCode" releaseCode
                                ]
                        ])
            ]

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

    let private holdSummary =
        WireSchema.objectOf
            [
                WireSchema.property "holdId" WireSchema.uuid
                WireSchema.property "reviewOn" WireSchema.date
            ]

    let private reviewValue =
        WireSchema.objectOf
            [
                WireSchema.property "caseId" WireSchema.uuid
                WireSchema.property "purgeEventId" WireSchema.uuid
                WireSchema.property "purgeWitnessSequence" sequence
                WireSchema.property "purgeWitnessEpoch" sequence
                WireSchema.property "purgeWitnessHash" digest
                WireSchema.property "cutoffSequence" sequence
                WireSchema.property "cutoffHash" digest
                WireSchema.property "targetCount" sequence
                WireSchema.property "targetDigest" digest
                WireSchema.property
                    "privacyPhase"
                    (WireSchema.enumeration
                        [
                            "ERASURE_PENDING"
                            "PAYLOAD_ERASED_SUPPRESSION_RETAINED"
                            "ERASURE_FINAL"
                        ])
                WireSchema.property "witnessPayloadPruned" Schema.boolean
                WireSchema.property "managedCopyCertificationPending" Schema.boolean
                WireSchema.property "authorityRevision" sequence
                WireSchema.property "authorityHash" digest
                WireSchema.property "activeHolds" (Schema.array holdSummary None (Some 256))
                WireSchema.property
                    "requiredDistinctStewardApprovals"
                    (Schema.oneOf [ WireSchema.number 0; WireSchema.number 2 ])
            ]

    let reviewResponse =
        envelope
            "tombstone.review"
            [
                outcome "AVAILABLE" reviewValue
                outcome "RESOURCE_UNAVAILABLE" Schema.nullValue
                outcome "CANCELLED" Schema.nullValue
                outcome "FAILED" (Schema.reference "Fault")
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
                            WireSchema.property "authorityRevision" sequence
                        ])
                outcome
                    "REFUSED"
                    (WireSchema.objectOf
                        [ WireSchema.property "reason" WebLifecycleSchemas.refusal ])
                outcome "RESOURCE_UNAVAILABLE" Schema.nullValue
                outcome
                    "CANCELLED_BEFORE_ADMISSION"
                    (WireSchema.objectOf [ WireSchema.property "eventId" WireSchema.uuid ])
                outcome "FAILED" (Schema.reference "Fault")
                outcome
                    "UNCONFIRMED"
                    (WireSchema.objectOf [ WireSchema.property "eventId" WireSchema.uuid ])
            ]
