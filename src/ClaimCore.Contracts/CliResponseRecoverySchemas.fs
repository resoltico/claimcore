namespace ClaimCore.Contracts

[<RequireQualifiedAccess>]
module internal CliResponseRecoverySchemas =
    let export =
        CliResponseSchemaCommon.choice
            [
                WireSchema.kind
                    "exported"
                    [
                        WireSchema.property "operationId" WireSchema.uuid
                        WireSchema.property "requestSha256" WireSchema.digest
                        WireSchema.property
                            "mediaType"
                            (WireSchema.token "application/vnd.claimcore.recovery+json")
                    ]
                CliResponseSchemaCommon.operationIdentity "notFound"
                WireSchema.kind
                    "rejected"
                    [ WireSchema.property "rejection" RecoveryValueSchemas.rejection ]
                WireSchema.kind "failed" [ WireSchema.property "fault" CoreValueSchemas.fault ]
                WireSchema.kind "cancelled" []
            ]

    let importPreview semantic =
        WireSchema.kind
            "previewed"
            [
                WireSchema.property "preview" (RecoveryValueSchemas.importPreviewCli semantic)
            ]
        |> fun success -> CliResponseSchemaCommon.query success RecoveryValueSchemas.rejection

    let importRetain semantic =
        let details kind =
            WireSchema.kind
                kind
                [ WireSchema.property "details" (RecoveryValueSchemas.detailsCli semantic) ]

        CliResponseSchemaCommon.choice
            [
                details "retained"
                details "existing"
                WireSchema.kind
                    "observedAccepted"
                    [ WireSchema.property "receipt" (CoreValueSchemas.cliReceipt semantic false) ]
                WireSchema.kind
                    "rejected"
                    [ WireSchema.property "rejection" RecoveryValueSchemas.rejection ]
                WireSchema.kind "failed" [ WireSchema.property "fault" CoreValueSchemas.fault ]
                WireSchema.kind "cancelledBeforeAdmission" []
                WireSchema.kind
                    "retainStateUnknown"
                    [
                        WireSchema.property
                            "artifactKind"
                            (WireSchema.enumeration [ "ENVELOPE"; "CANONICAL_RECORD" ])
                        WireSchema.property "sourceSha256" WireSchema.digest
                        WireSchema.property "operationId" (Schema.nullable WireSchema.uuid)
                        WireSchema.property "fault" CoreValueSchemas.fault
                    ]
            ]
