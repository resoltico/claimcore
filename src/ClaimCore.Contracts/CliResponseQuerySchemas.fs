namespace ClaimCore.Contracts

[<RequireQualifiedAccess>]
module internal CliResponseQuerySchemas =
    let caseGet semantic =
        CliResponseSchemaCommon.lookup
            [ WireSchema.property "value" (CoreValueSchemas.currentCase semantic) ]
            "caseReference"
            WireSchema.text
        |> fun success -> CliResponseSchemaCommon.query success CoreValueSchemas.rejection

    let caseList =
        WireSchema.kind
            "succeeded"
            [
                WireSchema.property "items" (WireSchema.array CoreValueSchemas.caseSummary)
                WireSchema.property "nextCursor" WireSchema.nullableText
            ]
        |> fun success -> CliResponseSchemaCommon.query success CoreValueSchemas.rejection

    let private historyEntry semantic =
        CliResponseSchemaCommon.choice
            [
                WireSchema.kind
                    "summary"
                    [
                        WireSchema.property "operationId" WireSchema.uuid
                        WireSchema.property "revision" WireSchema.revision
                        WireSchema.property "command" CoreValueSchemas.command
                        WireSchema.property "recordedAt" WireSchema.timestamp
                        WireSchema.property "recordedBy" WireSchema.text
                    ]
                WireSchema.kind
                    "full"
                    [ WireSchema.property "receipt" (CoreValueSchemas.cliReceipt semantic true) ]
            ]

    let history semantic =
        let found =
            WireSchema.kind
                "found"
                [
                    WireSchema.property "entries" (WireSchema.array (historyEntry semantic))
                    WireSchema.property "nextCursor" WireSchema.nullableText
                ]

        CliResponseSchemaCommon.choice
            [
                found
                WireSchema.kind "notFound" [ WireSchema.property "caseReference" WireSchema.text ]
            ]
        |> fun success -> CliResponseSchemaCommon.query success CoreValueSchemas.rejection

    let observe semantic =
        CliResponseSchemaCommon.lookup
            [ WireSchema.property "receipt" (CoreValueSchemas.cliReceipt semantic true) ]
            "operationId"
            WireSchema.uuid
        |> fun success -> CliResponseSchemaCommon.query success CoreValueSchemas.rejection

    let recoveryList =
        let item =
            CliResponseSchemaCommon.choice
                [
                    WireSchema.kind
                        "retained"
                        [ WireSchema.property "summary" RecoveryValueSchemas.summary ]
                    WireSchema.kind
                        "revoked"
                        [ WireSchema.property "revocation" RecoveryValueSchemas.revokedOperation ]
                ]

        WireSchema.kind
            "succeeded"
            [
                WireSchema.property "view" (WireSchema.enumeration [ "PENDING"; "TERMINAL" ])
                WireSchema.property "items" (WireSchema.array item)
                WireSchema.property "nextCursor" WireSchema.nullableText
                WireSchema.property "pendingPreparationCount" (Schema.integer (Some 0L) None)
                WireSchema.property "pendingCanonicalRequestBytes" (Schema.integer (Some 0L) None)
                WireSchema.property "maximumPendingPreparations" (Schema.integer (Some 1L) None)
                WireSchema.property
                    "maximumPendingCanonicalRequestBytes"
                    (Schema.integer (Some 1L) None)
                WireSchema.property "nearCapacity" Schema.boolean
            ]
        |> fun success -> CliResponseSchemaCommon.query success RecoveryValueSchemas.rejection

    let recoveryInspect semantic =
        let observation =
            CliResponseSchemaCommon.lookup
                [ WireSchema.property "receipt" (CoreValueSchemas.cliReceipt semantic true) ]
                "operationId"
                WireSchema.uuid

        let inspection =
            CliResponseSchemaCommon.choice
                [
                    WireSchema.kind
                        "retained"
                        [
                            WireSchema.property
                                "preparation"
                                (RecoveryValueSchemas.detailsCli semantic)
                            WireSchema.property "observation" observation
                        ]
                    WireSchema.kind
                        "revoked"
                        [ WireSchema.property "revocation" RecoveryValueSchemas.revokedOperation ]
                ]

        CliResponseSchemaCommon.lookup
            [ WireSchema.property "inspection" inspection ]
            "operationId"
            WireSchema.uuid
        |> fun success -> CliResponseSchemaCommon.query success RecoveryValueSchemas.rejection
