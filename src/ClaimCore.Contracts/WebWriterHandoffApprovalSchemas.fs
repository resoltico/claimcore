namespace ClaimCore.Contracts

/// One actor-bound approval of an exact, independently fenced writer handoff.
module internal WebWriterHandoffApprovalSchemas =
    let request =
        WireSchema.objectOf
            [
                WireSchema.property "approvalId" WireSchema.uuid
                WireSchema.property "handoffId" WireSchema.uuid
                WireSchema.property "oldGeneration" WireSchema.revision
                WireSchema.property "expectedWitnessSequence" WireSchema.revision
                WireSchema.property "expectedWitnessHash" WireSchema.digest
                WireSchema.property "newCapabilitySha256" WireSchema.digest
                WireSchema.property "checkpointSigningKeyId" WireSchema.uuid
                WireSchema.property "fenceReportSha256" WireSchema.digest
                WireSchema.property "inventorySha256" WireSchema.digest
                WireSchema.property "expiresAt" WireSchema.microsecondTimestamp
            ]

    let private outcome tag data =
        WireSchema.objectOf
            [
                WireSchema.property "tag" (WireSchema.token tag)
                WireSchema.property "data" data
            ]

    let response =
        WireSchema.objectOf
            [
                WireSchema.property "endpoint" (WireSchema.token "authority.approveWriterHandoff")
                WireSchema.property
                    "outcome"
                    (Schema.oneOf
                        [
                            outcome
                                "APPROVED"
                                (WireSchema.objectOf
                                    [
                                        WireSchema.property "approvalId" WireSchema.uuid
                                        WireSchema.property "authorityRevision" WireSchema.revision
                                    ])
                            outcome "RESOURCE_UNAVAILABLE" Schema.nullValue
                            outcome
                                "STARTED_UNCONFIRMED"
                                (WireSchema.objectOf
                                    [ WireSchema.property "approvalId" WireSchema.uuid ])
                        ])
            ]
