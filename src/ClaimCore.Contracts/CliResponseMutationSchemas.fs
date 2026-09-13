namespace ClaimCore.Contracts

[<RequireQualifiedAccess>]
module internal CliResponseMutationSchemas =
    let prepare semantic =
        CliResponseSchemaCommon.choice
            [
                WireSchema.kind
                    "prepared"
                    [
                        WireSchema.property "details" (RecoveryValueSchemas.detailsCli semantic)
                        WireSchema.property "review" (CoreValueSchemas.review semantic "fieldDiff")
                    ]
                WireSchema.kind
                    "observedAccepted"
                    [
                        WireSchema.property "details" (RecoveryValueSchemas.detailsCli semantic)
                        WireSchema.property "receipt" (CoreValueSchemas.cliReceipt semantic false)
                    ]
                WireSchema.kind
                    "retainedForRecovery"
                    [
                        WireSchema.property "details" (RecoveryValueSchemas.detailsCli semantic)
                        WireSchema.property "rejection" CoreValueSchemas.rejection
                    ]
                CliResponseSchemaCommon.operationFailure
                    "rejected"
                    "rejection"
                    CoreValueSchemas.rejection
                CliResponseSchemaCommon.operationFailure "failed" "fault" CoreValueSchemas.fault
                CliResponseSchemaCommon.operationIdentity "cancelledBeforeAdmission"
                WireSchema.kind
                    "preparationStateUnknown"
                    [
                        WireSchema.property "operationId" WireSchema.uuid
                        WireSchema.property "requestSha256" WireSchema.digest
                        WireSchema.property "fault" CoreValueSchemas.fault
                    ]
            ]

    let submission semantic =
        CliResponseSchemaCommon.choice
            [
                WireSchema.kind
                    "observedAccepted"
                    [ WireSchema.property "receipt" (CoreValueSchemas.cliReceipt semantic false) ]
                CliResponseSchemaCommon.completed semantic
                CliResponseSchemaCommon.beforeAttempt
                    "rejectedBeforeAttempt"
                    "rejection"
                    CoreValueSchemas.rejection
                CliResponseSchemaCommon.beforeAttempt
                    "failedBeforeAttempt"
                    "fault"
                    CoreValueSchemas.fault
                WireSchema.kind
                    "preparationStateUnknown"
                    [
                        WireSchema.property "operationId" WireSchema.uuid
                        WireSchema.property "requestSha256" WireSchema.digest
                        WireSchema.property "fault" CoreValueSchemas.fault
                    ]
                CliResponseSchemaCommon.operationIdentity "cancelledBeforeAdmission"
                CliResponseSchemaCommon.preparationOnly "cancelledBeforeAttempt"
                CliResponseSchemaCommon.preparationFailure "attemptAdmissionUnknown"
                CliResponseSchemaCommon.unresolved "attemptUnresolved"
            ]

    let resolve semantic =
        CliResponseSchemaCommon.choice
            [
                WireSchema.kind
                    "observedAccepted"
                    [ WireSchema.property "receipt" (CoreValueSchemas.cliReceipt semantic false) ]
                CliResponseSchemaCommon.completed semantic
                CliResponseSchemaCommon.beforeAttempt
                    "refusedBeforeAttempt"
                    "rejection"
                    RecoveryValueSchemas.rejection
                CliResponseSchemaCommon.beforeAttempt
                    "failedBeforeAttempt"
                    "fault"
                    CoreValueSchemas.fault
                CliResponseSchemaCommon.operationIdentity "cancelledBeforeAdmission"
                CliResponseSchemaCommon.preparationOnly "cancelledBeforeAttempt"
                CliResponseSchemaCommon.preparationFailure "attemptAdmissionUnknown"
                CliResponseSchemaCommon.unresolved "attemptUnresolved"
            ]

    let dismiss semantic =
        CliResponseSchemaCommon.choice
            [
                WireSchema.kind
                    "dismissed"
                    [ WireSchema.property "details" (RecoveryValueSchemas.detailsCli semantic) ]
                WireSchema.kind
                    "alreadyDismissed"
                    [ WireSchema.property "details" (RecoveryValueSchemas.detailsCli semantic) ]
                CliResponseSchemaCommon.operationIdentity "notFound"
                WireSchema.kind
                    "refused"
                    [
                        WireSchema.property
                            "details"
                            (Schema.nullable (RecoveryValueSchemas.detailsCli semantic))
                        WireSchema.property "rejection" RecoveryValueSchemas.rejection
                    ]
                WireSchema.kind "failed" [ WireSchema.property "fault" CoreValueSchemas.fault ]
                CliResponseSchemaCommon.operationIdentity "cancelledBeforeAdmission"
                WireSchema.kind
                    "dismissStateUnknown"
                    [
                        WireSchema.property "operationId" WireSchema.uuid
                        WireSchema.property "requestSha256" WireSchema.digest
                        WireSchema.property "fault" CoreValueSchemas.fault
                    ]
            ]
