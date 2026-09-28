namespace ClaimCore.Contracts

/// An actor approves exact opaque terminal metadata; this endpoint never certifies absence.
module internal WebTerminalApprovalSchemas =
    let private copy =
        WireSchema.objectOf
            [
                WireSchema.property "eventId" WireSchema.uuid
                WireSchema.property "caseId" WireSchema.uuid
                WireSchema.property "expectedAuthorityRevision" WireSchema.revision
                WireSchema.property "expectedAuthorityHash" WireSchema.digest
                WireSchema.property "installationId" WireSchema.uuid
                WireSchema.property "lineageId" WireSchema.uuid
                WireSchema.property "witnessEpoch" WireSchema.revision
                WireSchema.property "pruneEventId" WireSchema.uuid
                WireSchema.property "witnessCutoffSequence" WireSchema.revision
                WireSchema.property "witnessCutoffHash" WireSchema.digest
                WireSchema.property "copyInventoryDigest" WireSchema.digest
                WireSchema.property "relevantCopyCount" WireSchema.revision
                WireSchema.property "expectedWriterGeneration" WireSchema.revision
                WireSchema.property "policyId" (Schema.string None None (Some 1) (Some 128))
                WireSchema.property "suppressionUntil" WireSchema.timestamp
                WireSchema.property "validUntil" WireSchema.timestamp
            ]

    let private proposal =
        Schema.oneOf
            [
                WireSchema.kind
                    "CONFIRM_MANAGED_PAYLOAD_ABSENCE"
                    [ WireSchema.property "copy" copy ]
                WireSchema.kind
                    "COMPLETE_SUPPRESSION_HORIZON"
                    [
                        WireSchema.property "copy" copy
                        WireSchema.property "recoveryFenceDigest" WireSchema.digest
                        WireSchema.property "oldWriterGeneration" WireSchema.revision
                        WireSchema.property "newWriterGeneration" WireSchema.revision
                    ]
            ]

    let request =
        WireSchema.objectOf
            [
                WireSchema.property "proposal" proposal
                WireSchema.property "approvalId" WireSchema.uuid
                WireSchema.property "expiresAt" WireSchema.timestamp
            ]
