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
        WireSchema.kind
            "succeeded"
            [
                WireSchema.property "items" (WireSchema.array RecoveryValueSchemas.summary)
                WireSchema.property "nextCursor" WireSchema.nullableText
            ]
        |> fun success -> CliResponseSchemaCommon.query success RecoveryValueSchemas.rejection

    let recoveryInspect semantic =
        let observation =
            CliResponseSchemaCommon.lookup
                [ WireSchema.property "receipt" (CoreValueSchemas.cliReceipt semantic true) ]
                "operationId"
                WireSchema.uuid

        CliResponseSchemaCommon.lookup
            [
                WireSchema.property "preparation" (RecoveryValueSchemas.detailsCli semantic)
                WireSchema.property "observation" observation
            ]
            "operationId"
            WireSchema.uuid
        |> fun success -> CliResponseSchemaCommon.query success RecoveryValueSchemas.rejection
