namespace ClaimCore.Contracts

module internal WebSignerApprovalSchemas =
    let private common =
        [
            WireSchema.property "approvalId" WireSchema.uuid
            WireSchema.property "signingKeyId" WireSchema.uuid
            WireSchema.property "action" (WireSchema.enumeration [ "REGISTER"; "RETIRE" ])
            WireSchema.property
                "purpose"
                (WireSchema.enumeration
                    [
                        "COPY_ATTESTOR"
                        "LOCATION_REGISTRY"
                        "LOCATION_INSPECTOR"
                        "DELETION_VERIFIER"
                        "RESTORE_COPY_VERIFIER"
                        "RESTORE_REPORT"
                        "CHECKPOINT"
                        "WRITER_HANDOFF_ABORT"
                        "INSTALLATION_LOSS_RETIREMENT"
                    ])
            WireSchema.property "publicKeySha256" WireSchema.digest
            WireSchema.property "expiresAt" WireSchema.microsecondTimestamp
        ]

    let request =
        Schema.oneOf
            [
                WireSchema.objectOf (
                    common
                    @ [
                        WireSchema.property "approvalRole" (WireSchema.token "CUSTODIAN")
                        WireSchema.property "holderApprovalId" Schema.nullValue
                    ]
                )
                WireSchema.objectOf (
                    common
                    @ [
                        WireSchema.property "approvalRole" (WireSchema.token "OWNER")
                        WireSchema.property "holderApprovalId" WireSchema.uuid
                    ]
                )
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
                WireSchema.property "endpoint" (WireSchema.token "authority.approveCopySigner")
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
