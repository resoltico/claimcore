namespace ClaimCore.Contracts

/// A case-scoped human owner approves exact custody metadata, never raw location or absence.
module internal WebCopyAdoptionApprovalSchemas =
    let private origin =
        Schema.oneOf
            [
                WireSchema.kind
                    "PRODUCT_EXPORT"
                    [
                        WireSchema.property "exportId" WireSchema.uuid
                        WireSchema.property "receiptSequence" WireSchema.revision
                        WireSchema.property "receiptHash" WireSchema.digest
                    ]
                WireSchema.kind
                    "ADOPTED_EXTERNAL"
                    [
                        WireSchema.property "registrySequence" WireSchema.revision
                        WireSchema.property "registryHash" WireSchema.digest
                    ]
            ]

    let request =
        WireSchema.objectOf
            [
                WireSchema.property "approvalId" WireSchema.uuid
                WireSchema.property "adoptionEventId" WireSchema.uuid
                WireSchema.property "copyId" WireSchema.uuid
                WireSchema.property "caseId" WireSchema.uuid
                WireSchema.property "origin" origin
                WireSchema.property "ciphertextSha256" WireSchema.digest
                WireSchema.property "ciphertextBytes" WireSchema.revision
                WireSchema.property "capturedAt" WireSchema.microsecondTimestamp
                WireSchema.property "retainUntil" WireSchema.microsecondTimestamp
                WireSchema.property "locationCommitment" WireSchema.digest
                WireSchema.property "custodianCommitment" WireSchema.digest
                WireSchema.property "custodianSigningKeyId" WireSchema.uuid
                WireSchema.property "registrySigningKeyId" WireSchema.uuid
                WireSchema.property "inspectorSigningKeyId" WireSchema.uuid
                WireSchema.property "custodianCanonicalSha256" WireSchema.digest
                WireSchema.property "registryCanonicalSha256" WireSchema.digest
                WireSchema.property "inspectionReportSha256" WireSchema.digest
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
                WireSchema.property "endpoint" (WireSchema.token "authority.approveCopyAdoption")
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
