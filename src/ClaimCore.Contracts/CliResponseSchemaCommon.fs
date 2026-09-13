namespace ClaimCore.Contracts

[<RequireQualifiedAccess>]
module internal CliResponseSchemaCommon =
    let choice values = Schema.oneOf values

    let query succeeded rejection =
        choice
            [
                succeeded
                WireSchema.kind "rejected" [ WireSchema.property "rejection" rejection ]
                WireSchema.kind "failed" [ WireSchema.property "fault" CoreValueSchemas.fault ]
                WireSchema.kind "cancelled" []
            ]

    let lookup foundProperties identityName identity =
        choice
            [
                WireSchema.kind "found" foundProperties
                WireSchema.kind "notFound" [ WireSchema.property identityName identity ]
            ]

    let operationIdentity kind =
        WireSchema.kind kind [ WireSchema.property "operationId" WireSchema.uuid ]

    let operationFailure kind name detail =
        WireSchema.kind
            kind
            [
                WireSchema.property "operationId" WireSchema.uuid
                WireSchema.property name detail
            ]

    let private nullablePreparation = Schema.nullable RecoveryValueSchemas.summary

    let private definiteExecution semantic =
        choice
            [
                WireSchema.kind
                    "accepted"
                    [ WireSchema.property "receipt" (CoreValueSchemas.cliReceipt semantic false) ]
                WireSchema.kind
                    "rejected"
                    [
                        WireSchema.property "operationId" WireSchema.uuid
                        WireSchema.property "rejection" CoreValueSchemas.rejection
                    ]
                WireSchema.kind
                    "failedBeforeCommit"
                    [
                        WireSchema.property "operationId" WireSchema.uuid
                        WireSchema.property "fault" CoreValueSchemas.fault
                    ]
            ]

    let completed semantic =
        WireSchema.kind
            "completed"
            [
                WireSchema.property "preparation" RecoveryValueSchemas.summary
                WireSchema.property "attemptId" WireSchema.uuid
                WireSchema.property "execution" (definiteExecution semantic)
                WireSchema.property
                    "settlement"
                    (WireSchema.enumeration [ "CONFIRMED"; "UNCONFIRMED" ])
            ]

    let beforeAttempt kind name detail =
        WireSchema.kind
            kind
            [
                WireSchema.property "preparation" nullablePreparation
                WireSchema.property name detail
            ]

    let preparationOnly kind =
        WireSchema.kind kind [ WireSchema.property "preparation" RecoveryValueSchemas.summary ]

    let preparationFailure kind =
        WireSchema.kind
            kind
            [
                WireSchema.property "preparation" RecoveryValueSchemas.summary
                WireSchema.property "fault" CoreValueSchemas.fault
            ]

    let unresolved kind =
        WireSchema.kind
            kind
            [
                WireSchema.property "preparation" RecoveryValueSchemas.summary
                WireSchema.property "attemptId" WireSchema.uuid
                WireSchema.property "fault" CoreValueSchemas.fault
            ]
