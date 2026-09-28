namespace ClaimCore.Contracts

/// An individual verifier approves one exact signed absence report and witness cutoff.
module internal WebCopyDeletionApprovalSchemas =
    let request =
        WireSchema.objectOf
            [
                WireSchema.property "approvalId" WireSchema.uuid
                WireSchema.property "deletionEventId" WireSchema.uuid
                WireSchema.property "copyId" WireSchema.uuid
                WireSchema.property "verifierSigningKeyId" WireSchema.uuid
                WireSchema.property "expectedCopyRevision" WireSchema.revision
                WireSchema.property "locationCommitment" WireSchema.digest
                WireSchema.property "inspectionReportSha256" WireSchema.digest
                WireSchema.property "witnessCutoffSequence" WireSchema.revision
                WireSchema.property "witnessCutoffHash" WireSchema.digest
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
                WireSchema.property "endpoint" (WireSchema.token "authority.approveCopyDeletion")
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
