namespace ClaimCore.Contracts

open ClaimCore.Application

[<RequireQualifiedAccess>]
module WebSchemaDefinitions =
    let hostFailureStatuses = [ 400; 401; 403; 404; 409; 413; 415; 429; 500; 503 ]

    let private reference = Schema.reference

    let hostFailure =
        WireSchema.objectOf
            [
                WireSchema.property "kind" (WireSchema.token "HOST_FAILURE")
                WireSchema.property "code" WireSchema.text
                WireSchema.property "message" WireSchema.text
                WireSchema.property
                    "executionPhase"
                    (Schema.nullable (
                        WireSchema.enumeration [ "NOT_STARTED"; "STARTED_UNCONFIRMED" ]
                    ))
            ]

    let sessionSnapshot =
        WireSchema.objectOf
            [
                WireSchema.property "authenticated" Schema.boolean
                WireSchema.property "antiforgeryToken" WireSchema.nullableText
            ]

    let private caseView =
        WireSchema.objectOf
            [
                WireSchema.property "fields" (reference "CaseFields")
                WireSchema.property "revision" WireSchema.revision
            ]

    let private currentCase =
        WireSchema.objectOf
            [
                WireSchema.property "case" (reference "CaseView")
                WireSchema.property "availableCommands" (WireSchema.array CoreValueSchemas.command)
            ]

    let private receipt =
        WireSchema.objectOf
            [
                WireSchema.property "operationId" WireSchema.uuid
                WireSchema.property "snapshot" (reference "CaseView")
                WireSchema.property "recordedAt" WireSchema.timestamp
                WireSchema.property "recordedBy" WireSchema.text
                WireSchema.property "replayed" Schema.boolean
                WireSchema.property "command" CoreValueSchemas.command
            ]

    let private historyEntry =
        Schema.oneOf
            [
                WireSchema.tagged
                    "SUMMARY"
                    [ WireSchema.property "change" (reference "ChangeSummary") ]
                WireSchema.tagged "FULL" [ WireSchema.property "receipt" (reference "Receipt") ]
            ]

    let private definitionPayload =
        WireSchema.objectOf
            [
                WireSchema.property "semanticFingerprint" WireSchema.digest
                WireSchema.property "webFingerprint" WireSchema.digest
                WireSchema.property "runtime" (reference "RuntimeContext")
                WireSchema.property "definition" (reference "SemanticDefinition")
            ]

    let private preparationSummary = RecoveryValueSchemas.summary

    let private authoredValue =
        WireSchema.objectOf
            [
                WireSchema.property "name" WireSchema.text
                WireSchema.property "value" WireSchema.text
            ]

    let private attempt =
        WireSchema.objectOf
            [
                WireSchema.property "attemptId" WireSchema.uuid
                WireSchema.property "startedAt" WireSchema.timestamp
                WireSchema.property
                    "settlement"
                    (Schema.nullable (WireSchema.enumeration [ "ACCEPTED"; "REJECTED"; "ERROR" ]))
                WireSchema.property "settledAt" (Schema.nullable WireSchema.timestamp)
            ]

    let private preparationDetails (semantic: SemanticCoreContract) =
        WireSchema.objectOf
            [
                WireSchema.property "summary" (reference "PreparationSummary")
                WireSchema.property "expectedRevision" WireSchema.revision
                WireSchema.property "authoredValues" (WireSchema.array authoredValue)
                WireSchema.property
                    "canonicalCommandFormat"
                    (WireSchema.number semantic.CanonicalCommandFormat)
                WireSchema.property "preparingApplicationVersion" WireSchema.text
                WireSchema.property "preparingContractFingerprint" WireSchema.digest
                WireSchema.property
                    "preparingContractKind"
                    (WireSchema.enumeration [ "LEGACY_UNCLASSIFIED"; "SEMANTIC_CORE_V1" ])
                WireSchema.property "attempts" (WireSchema.array attempt)
                WireSchema.property "legacyUncertainty" Schema.boolean
            ]

    let private advisoryReview =
        WireSchema.objectOf
            [
                WireSchema.property "before" (Schema.nullable (reference "CaseView"))
                WireSchema.property "proposed" (reference "CaseView")
                WireSchema.property "changes" (WireSchema.array (reference "FieldDiff"))
                WireSchema.property "context" (reference "RuntimeContext")
                WireSchema.property "advisory" Schema.boolean
            ]

    let private importEffect (semantic: SemanticCoreContract) =
        WireSchema.objectOf
            [
                WireSchema.property "operationId" WireSchema.uuid
                WireSchema.property "caseReference" WireSchema.text
                WireSchema.property "command" CoreValueSchemas.command
                WireSchema.property "expectedRevision" WireSchema.revision
                WireSchema.property "authoredValues" (WireSchema.array authoredValue)
                WireSchema.property
                    "canonicalCommandFormat"
                    (WireSchema.number semantic.CanonicalCommandFormat)
                WireSchema.property "requestSha256" WireSchema.digest
            ]

    let private importPreview semantic =
        WireSchema.objectOf
            [
                WireSchema.property
                    "artifactKind"
                    (WireSchema.enumeration [ "ENVELOPE"; "UNBOUND_CANONICAL_RECORD" ])
                WireSchema.property "sourceSha256" WireSchema.digest
                WireSchema.property "decodedEffect" (importEffect semantic)
                WireSchema.property
                    "existingPreparation"
                    (Schema.nullable (reference "PreparationSummary"))
            ]

    let private recoveryObservation =
        Schema.oneOf
            [
                WireSchema.tagged "FOUND" [ WireSchema.property "value" (reference "Receipt") ]
                WireSchema.tagged "NOT_FOUND" [ WireSchema.property "identity" WireSchema.uuid ]
            ]

    let private recoveryDetails =
        WireSchema.objectOf
            [
                WireSchema.property "preparation" (reference "PreparationDetails")
                WireSchema.property "observation" recoveryObservation
            ]

    let private definiteExecution =
        Schema.oneOf
            [
                WireSchema.tagged "ACCEPTED" [ WireSchema.property "receipt" (reference "Receipt") ]
                WireSchema.tagged
                    "REJECTED"
                    [
                        WireSchema.property "operationId" WireSchema.uuid
                        WireSchema.property "rejection" (reference "Rejection")
                    ]
                WireSchema.tagged
                    "FAILED_BEFORE_COMMIT"
                    [
                        WireSchema.property "operationId" WireSchema.uuid
                        WireSchema.property "fault" (reference "Fault")
                    ]
            ]

    let all semantic semanticDefinition =
        [
            "HostFailure", hostFailure
            "Rejection", CoreValueSchemas.rejection
            "RecoveryRejection", RecoveryValueSchemas.rejection
            "Fault", CoreValueSchemas.fault
            "CaseFields", CoreValueSchemas.caseFields semantic
            "CaseView", caseView
            "CurrentCase", currentCase
            "CaseSummary", CoreValueSchemas.caseSummary
            "Receipt", receipt
            "ChangeSummary", CoreValueSchemas.changeSummary
            "HistoryEntry", historyEntry
            "RuntimeContext", CoreValueSchemas.runtimeContext
            "FieldDiff", CoreValueSchemas.fieldDiff
            "SemanticDefinition", semanticDefinition
            "DefinitionPayload", definitionPayload
            "SessionSnapshot", sessionSnapshot
            "PreparationSummary", preparationSummary
            "PreparationDetails", preparationDetails semantic
            "AdvisoryReview", advisoryReview
            "RecoveryImportPreview", importPreview semantic
            "RecoveryDetails", recoveryDetails
            "DefiniteExecution", definiteExecution
        ]
