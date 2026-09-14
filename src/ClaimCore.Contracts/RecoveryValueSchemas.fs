namespace ClaimCore.Contracts

open ClaimCore.Application

[<RequireQualifiedAccess>]
module RecoveryValueSchemas =
    let rejectionCode = WireTokens.recoveryRejectionCodes |> WireSchema.enumeration

    let rejection =
        WireSchema.objectOf
            [
                WireSchema.property "code" rejectionCode
                WireSchema.property "message" WireSchema.text
                WireSchema.property "recommendedAction" CoreValueSchemas.action
            ]

    let private states =
        [
            PreparationState.Unsubmitted
            PreparationState.SubmissionStarted
            PreparationState.Dismissed
            PreparationState.Revoked
        ]
        |> List.map WireTokens.preparationState
        |> WireSchema.enumeration

    let private actions =
        [ RecoveryAction.Resolve; RecoveryAction.Dismiss; RecoveryAction.Export ]
        |> List.map WireTokens.recoveryAction
        |> WireSchema.enumeration

    let private authority =
        [
            RecoveryAuthority.PendingAuthority
            RecoveryAuthority.AcceptedAuthority
            RecoveryAuthority.RevokedAuthority
        ]
        |> List.map WireTokens.recoveryAuthority
        |> WireSchema.enumeration

    let summary =
        WireSchema.objectOf
            [
                WireSchema.property "operationId" WireSchema.uuid
                WireSchema.property "caseReference" WireSchema.text
                WireSchema.property "command" CoreValueSchemas.command
                WireSchema.property "preparedAt" WireSchema.timestamp
                WireSchema.property "state" states
                WireSchema.property "authority" authority
                WireSchema.property "requestSha256" (Schema.nullable WireSchema.digest)
                WireSchema.property "availableActions" (WireSchema.array actions)
            ]

    let revokedOperation =
        WireSchema.objectOf
            [
                WireSchema.property "operationId" WireSchema.uuid
                WireSchema.property "revokedAt" WireSchema.timestamp
                WireSchema.property "reason" WireSchema.text
            ]

    let private authoredValue =
        WireSchema.objectOf
            [
                WireSchema.property "name" WireSchema.text
                WireSchema.property "value" WireSchema.text
            ]

    let authoredValuesWeb = WireSchema.array authoredValue
    let authoredValuesCli = Schema.dictionary WireSchema.text

    let private attempt =
        WireSchema.objectOf
            [
                WireSchema.property "attemptId" WireSchema.uuid
                WireSchema.property "startedAt" WireSchema.timestamp
                WireSchema.property
                    "settlement"
                    (Schema.nullable (
                        WireSchema.enumeration
                            [ "ACCEPTED"; "REJECTED"; "ERROR"; "REVOKED_BEFORE_EXECUTION" ]
                    ))
                WireSchema.property "settledAt" (Schema.nullable WireSchema.timestamp)
            ]

    let private attemptPage =
        WireSchema.objectOf
            [
                WireSchema.property "items" (WireSchema.array attempt)
                WireSchema.property "nextCursor" WireSchema.nullableText
                WireSchema.property "legacyUncertainty" Schema.boolean
            ]

    let details (semantic: SemanticCoreContract) authoredValues =
        WireSchema.objectOf
            [
                WireSchema.property "summary" summary
                WireSchema.property "expectedRevision" WireSchema.revision
                WireSchema.property "authoredValues" authoredValues
                WireSchema.property
                    "canonicalCommandFormat"
                    (WireSchema.number semantic.CanonicalCommandFormat)
                WireSchema.property "preparingApplicationVersion" WireSchema.text
                WireSchema.property "preparingContractFingerprint" WireSchema.digest
                WireSchema.property
                    "preparingContractKind"
                    (WireSchema.enumeration [ "LEGACY_UNCLASSIFIED"; "SEMANTIC_CORE_V1" ])
                WireSchema.property "attempts" attemptPage
            ]

    let detailsWeb semantic = details semantic authoredValuesWeb
    let detailsCli semantic = details semantic authoredValuesCli

    let private importEffect (semantic: SemanticCoreContract) authoredValues =
        WireSchema.objectOf
            [
                WireSchema.property "operationId" WireSchema.uuid
                WireSchema.property "caseReference" WireSchema.text
                WireSchema.property "command" CoreValueSchemas.command
                WireSchema.property "expectedRevision" WireSchema.revision
                WireSchema.property "authoredValues" authoredValues
                WireSchema.property
                    "canonicalCommandFormat"
                    (WireSchema.number semantic.CanonicalCommandFormat)
                WireSchema.property "requestSha256" WireSchema.digest
            ]

    let importPreview semantic artifactKinds authoredValues =
        WireSchema.objectOf
            [
                WireSchema.property "artifactKind" (WireSchema.enumeration artifactKinds)
                WireSchema.property "sourceSha256" WireSchema.digest
                WireSchema.property "decodedEffect" (importEffect semantic authoredValues)
                WireSchema.property "existingPreparation" (Schema.nullable summary)
            ]

    let importPreviewWeb semantic =
        importPreview semantic [ "ENVELOPE"; "UNBOUND_CANONICAL_RECORD" ] authoredValuesWeb

    let importPreviewCli semantic =
        importPreview semantic [ "ENVELOPE"; "CANONICAL_RECORD" ] authoredValuesCli

    let webObservation semantic =
        Schema.oneOf
            [
                WireSchema.tagged
                    "FOUND"
                    [ WireSchema.property "value" (CoreValueSchemas.webReceipt semantic) ]
                WireSchema.tagged "NOT_FOUND" [ WireSchema.property "identity" WireSchema.uuid ]
            ]

    let webDetails semantic =
        WireSchema.objectOf
            [
                WireSchema.property "preparation" (detailsWeb semantic)
                WireSchema.property "observation" (webObservation semantic)
            ]

    let definiteExecution receipt =
        Schema.oneOf
            [
                WireSchema.tagged "ACCEPTED" [ WireSchema.property "receipt" receipt ]
                WireSchema.tagged
                    "REJECTED"
                    [
                        WireSchema.property "operationId" WireSchema.uuid
                        WireSchema.property "rejection" CoreValueSchemas.rejection
                    ]
                WireSchema.tagged
                    "REVOKED_BEFORE_EXECUTION"
                    [ WireSchema.property "operationId" WireSchema.uuid ]
                WireSchema.tagged
                    "FAILED_BEFORE_COMMIT"
                    [
                        WireSchema.property "operationId" WireSchema.uuid
                        WireSchema.property "fault" CoreValueSchemas.fault
                    ]
            ]
