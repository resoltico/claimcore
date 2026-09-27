namespace ClaimCore.Contracts

/// Two human-owner approval actions over an exact witnessed, nonclaimant activation plan.
module internal WebRealDataActivationSchemas =
    let reviewRequest =
        WireSchema.objectOf [ WireSchema.property "planId" WireSchema.uuid ]

    let approvalRequest =
        WireSchema.objectOf
            [
                WireSchema.property "approvalId" WireSchema.uuid
                WireSchema.property "planId" WireSchema.uuid
                WireSchema.property "activationId" WireSchema.uuid
                WireSchema.property "installationId" WireSchema.uuid
                WireSchema.property "lineageId" WireSchema.uuid
                WireSchema.property "epoch" WireSchema.revision
                WireSchema.property "writerGeneration" WireSchema.revision
                WireSchema.property "activationPlanSha256" WireSchema.digest
                WireSchema.property "policySha256" WireSchema.digest
                WireSchema.property "reviewWitnessSequence" WireSchema.revision
                WireSchema.property "reviewWitnessHash" WireSchema.digest
                WireSchema.property "expectedWitnessSequence" WireSchema.revision
                WireSchema.property "expectedWitnessHash" WireSchema.digest
                WireSchema.property "expiresAt" WireSchema.timestamp
            ]

    let private outcome tag data =
        WireSchema.objectOf
            [
                WireSchema.property "tag" (WireSchema.token tag)
                WireSchema.property "data" data
            ]

    let private reviewFacts =
        WireSchema.objectOf
            [
                WireSchema.property "planId" WireSchema.uuid
                WireSchema.property "activationId" WireSchema.uuid
                WireSchema.property "installationId" WireSchema.uuid
                WireSchema.property "lineageId" WireSchema.uuid
                WireSchema.property "epoch" WireSchema.revision
                WireSchema.property "writerGeneration" WireSchema.revision
                WireSchema.property "policySha256" WireSchema.digest
                WireSchema.property "publicationRootSha256" WireSchema.digest
                WireSchema.property "cycleId" WireSchema.uuid
                WireSchema.property "leaseId" WireSchema.uuid
                WireSchema.property "captureReceiptSha256" WireSchema.digest
                WireSchema.property "primaryBaseCopyId" WireSchema.uuid
                WireSchema.property "witnessBaseCopyId" WireSchema.uuid
                WireSchema.property "primaryBasePhysicalReceiptSha256" WireSchema.digest
                WireSchema.property "witnessBasePhysicalReceiptSha256" WireSchema.digest
                WireSchema.property "checkpointObjectSha256" WireSchema.digest
                WireSchema.property "testRestoreReportSha256" WireSchema.digest
                WireSchema.property "testRestoreFullAuditSha256" WireSchema.digest
                WireSchema.property "minimumArtifactCutoffSequence" WireSchema.revision
                WireSchema.property "minimumPrimaryWalHorizon" WireSchema.text
                WireSchema.property "minimumWitnessWalHorizon" WireSchema.text
                WireSchema.property "canonicalPlan" (Schema.string None None (Some 1) (Some 16384))
                WireSchema.property "planSha256" WireSchema.digest
                WireSchema.property "publishedAt" WireSchema.timestamp
                WireSchema.property "publicationWitnessSequence" WireSchema.revision
                WireSchema.property "publicationWitnessHash" WireSchema.digest
                WireSchema.property "approvalExpiresNoLaterThan" WireSchema.timestamp
            ]

    let reviewResponse =
        WireSchema.objectOf
            [
                WireSchema.property
                    "endpoint"
                    (WireSchema.token "authority.reviewRealDataActivation")
                WireSchema.property
                    "outcome"
                    (Schema.oneOf
                        [
                            outcome "REVIEWED" reviewFacts
                            outcome "RESOURCE_UNAVAILABLE" Schema.nullValue
                        ])
            ]

    let approvalResponse =
        WireSchema.objectOf
            [
                WireSchema.property
                    "endpoint"
                    (WireSchema.token "authority.approveRealDataActivation")
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
