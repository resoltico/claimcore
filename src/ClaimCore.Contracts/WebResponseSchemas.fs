namespace ClaimCore.Contracts

open ClaimCore.Application

[<RequireQualifiedAccess>]
module WebResponseSchemas =
    let private reference = Schema.reference

    let private outcome tag data =
        WireSchema.objectOf
            [
                WireSchema.property "tag" (WireSchema.token tag)
                WireSchema.property "data" data
            ]

    let private outcomes endpoint cases =
        let tagged = cases |> List.map (fun (tag, data) -> outcome tag data) |> Schema.oneOf

        WireSchema.objectOf
            [
                WireSchema.property "endpoint" (WireSchema.token endpoint)
                WireSchema.property "outcome" tagged
            ]

    let private query endpoint succeeded rejection =
        outcomes
            endpoint
            [
                "SUCCEEDED", succeeded
                "REJECTED", rejection
                "FAILED", reference "Fault"
                "CANCELLED", Schema.nullValue
            ]

    let private lookup identityName identity foundName found =
        Schema.oneOf
            [
                WireSchema.tagged "FOUND" [ WireSchema.property foundName found ]
                WireSchema.tagged "NOT_FOUND" [ WireSchema.property identityName identity ]
            ]

    let private operationIdentity =
        WireSchema.objectOf [ WireSchema.property "operationId" WireSchema.uuid ]

    let private operationRejection =
        WireSchema.objectOf
            [
                WireSchema.property "operationId" WireSchema.uuid
                WireSchema.property "rejection" (reference "Rejection")
            ]

    let private operationFailure =
        WireSchema.objectOf
            [
                WireSchema.property "operationId" WireSchema.uuid
                WireSchema.property "fault" (reference "Fault")
            ]

    let private nullablePreparation = Schema.nullable (reference "PreparationSummary")

    let private completed =
        WireSchema.objectOf
            [
                WireSchema.property "preparation" (reference "PreparationSummary")
                WireSchema.property "attemptId" WireSchema.uuid
                WireSchema.property "execution" (reference "DefiniteExecution")
                WireSchema.property
                    "settlement"
                    (WireSchema.enumeration [ "CONFIRMED"; "UNCONFIRMED" ])
            ]

    let private resolveCases =
        [
            "OBSERVED_ACCEPTED",
            WireSchema.objectOf [ WireSchema.property "receipt" (reference "Receipt") ]
            "COMPLETED", completed
            "REFUSED_BEFORE_ATTEMPT",
            WireSchema.objectOf
                [
                    WireSchema.property "preparation" nullablePreparation
                    WireSchema.property "rejection" (reference "RecoveryRejection")
                ]
            "FAILED_BEFORE_ATTEMPT",
            WireSchema.objectOf
                [
                    WireSchema.property "preparation" nullablePreparation
                    WireSchema.property "fault" (reference "Fault")
                ]
            "CANCELLED_BEFORE_ADMISSION", operationIdentity
            "CANCELLED_BEFORE_ATTEMPT",
            WireSchema.objectOf
                [ WireSchema.property "preparation" (reference "PreparationSummary") ]
            "ATTEMPT_ADMISSION_UNKNOWN",
            WireSchema.objectOf
                [
                    WireSchema.property "preparation" (reference "PreparationSummary")
                    WireSchema.property "fault" (reference "Fault")
                ]
            "ATTEMPT_UNRESOLVED",
            WireSchema.objectOf
                [
                    WireSchema.property "preparation" (reference "PreparationSummary")
                    WireSchema.property "attemptId" WireSchema.uuid
                    WireSchema.property "fault" (reference "Fault")
                ]
        ]

    let private session endpoint =
        outcomes endpoint [ "SNAPSHOT", reference "SessionSnapshot" ]

    let private definition =
        outcomes "definition" [ "DESCRIBED", reference "DefinitionPayload" ]

    let private caseGet =
        let succeeded =
            lookup "caseReference" WireSchema.text "current" (reference "CurrentCase")

        query "case.get" succeeded (reference "Rejection")

    let private caseList =
        let page =
            WireSchema.objectOf
                [
                    WireSchema.property "items" (WireSchema.array (reference "CaseSummary"))
                    WireSchema.property "nextCursor" WireSchema.nullableText
                ]

        query "case.list" page (reference "Rejection")

    let private history =
        let succeeded =
            Schema.oneOf
                [
                    WireSchema.tagged
                        "FOUND"
                        [
                            WireSchema.property
                                "entries"
                                (WireSchema.array (reference "HistoryEntry"))
                            WireSchema.property "nextCursor" WireSchema.nullableText
                        ]
                    WireSchema.tagged
                        "NOT_FOUND"
                        [ WireSchema.property "caseReference" WireSchema.text ]
                ]

        query "case.history" succeeded (reference "Rejection")

    let private observe =
        let succeeded = lookup "operationId" WireSchema.uuid "receipt" (reference "Receipt")

        query "operation.observe" succeeded (reference "Rejection")

    let private prepare =
        outcomes
            "command.prepare"
            [
                "PREPARED",
                WireSchema.objectOf
                    [
                        WireSchema.property "details" (reference "PreparationDetails")
                        WireSchema.property "review" (reference "AdvisoryReview")
                    ]
                "OBSERVED_ACCEPTED",
                WireSchema.objectOf
                    [
                        WireSchema.property "details" (reference "PreparationDetails")
                        WireSchema.property "receipt" (reference "Receipt")
                    ]
                "RETAINED_FOR_RECOVERY",
                WireSchema.objectOf
                    [
                        WireSchema.property "details" (reference "PreparationDetails")
                        WireSchema.property "rejection" (reference "Rejection")
                    ]
                "REJECTED", operationRejection
                "FAILED", operationFailure
                "CANCELLED_BEFORE_ADMISSION", operationIdentity
                "PREPARATION_STATE_UNKNOWN",
                WireSchema.objectOf
                    [
                        WireSchema.property "operationId" WireSchema.uuid
                        WireSchema.property "requestSha256" WireSchema.digest
                        WireSchema.property "fault" (reference "Fault")
                    ]
            ]

    let private recoveryList =
        let page =
            WireSchema.objectOf
                [
                    WireSchema.property "items" (WireSchema.array (reference "PreparationSummary"))
                    WireSchema.property "nextCursor" WireSchema.nullableText
                ]

        query "recovery.list" page (reference "RecoveryRejection")

    let private recoveryInspect =
        let succeeded =
            lookup "identity" WireSchema.uuid "value" (reference "RecoveryDetails")

        query "recovery.inspect" succeeded (reference "RecoveryRejection")

    let private resolve endpoint = outcomes endpoint resolveCases

    let private dismiss =
        outcomes
            "recovery.dismiss"
            [
                "DISMISSED", reference "PreparationDetails"
                "ALREADY_DISMISSED", reference "PreparationDetails"
                "NOT_FOUND", operationIdentity
                "REFUSED",
                WireSchema.objectOf
                    [
                        WireSchema.property
                            "details"
                            (Schema.nullable (reference "PreparationDetails"))
                        WireSchema.property "rejection" (reference "RecoveryRejection")
                    ]
                "FAILED", reference "Fault"
                "CANCELLED_BEFORE_ADMISSION", operationIdentity
                "DISMISS_STATE_UNKNOWN",
                WireSchema.objectOf
                    [
                        WireSchema.property "operationId" WireSchema.uuid
                        WireSchema.property "requestSha256" WireSchema.digest
                        WireSchema.property "fault" (reference "Fault")
                    ]
            ]

    let private export =
        outcomes
            "recovery.export"
            [
                "NOT_FOUND", operationIdentity
                "REJECTED", reference "RecoveryRejection"
                "FAILED", reference "Fault"
                "CANCELLED", Schema.nullValue
            ]

    let private importPreview endpoint =
        query endpoint (reference "RecoveryImportPreview") (reference "RecoveryRejection")

    let private importRetain endpoint =
        outcomes
            endpoint
            [
                "RETAINED", reference "PreparationDetails"
                "EXISTING", reference "PreparationDetails"
                "REJECTED", reference "RecoveryRejection"
                "FAILED", reference "Fault"
                "CANCELLED_BEFORE_ADMISSION", Schema.nullValue
                "RETAIN_STATE_UNKNOWN",
                WireSchema.objectOf
                    [
                        WireSchema.property
                            "artifactKind"
                            (WireSchema.enumeration [ "ENVELOPE"; "UNBOUND_CANONICAL_RECORD" ])
                        WireSchema.property "sourceSha256" WireSchema.digest
                        WireSchema.property "operationId" (Schema.nullable WireSchema.uuid)
                        WireSchema.property "fault" (reference "Fault")
                    ]
            ]

    let all () =
        [
            "session", session "session"
            "session.login", session "session.login"
            "session.logout", session "session.logout"
            "definition", definition
            "case.get", caseGet
            "case.list", caseList
            "case.history", history
            "operation.observe", observe
            "command.prepare", prepare
            "command.execute", resolve "command.execute"
            "recovery.list", recoveryList
            "recovery.inspect", recoveryInspect
            "recovery.resolve", resolve "recovery.resolve"
            "recovery.dismiss", dismiss
            "recovery.export", export
            "recovery.importEnvelopePreview", importPreview "recovery.importEnvelopePreview"
            "recovery.importEnvelopeRetain", importRetain "recovery.importEnvelopeRetain"
            "recovery.importRecordPreview", importPreview "recovery.importRecordPreview"
            "recovery.importRecordRetain", importRetain "recovery.importRecordRetain"
        ]
