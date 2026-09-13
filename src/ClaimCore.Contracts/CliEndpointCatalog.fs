namespace ClaimCore.Contracts

open ClaimCore.Application
open ClaimCore.Domain

module internal CliEndpointCatalog =
    let private caseReference (semantic: SemanticCoreContract) =
        semantic.Fields |> List.find (fun field -> field.Name = "caseReference")

    let private commandEndpoints draft =
        [
            {
                Identifier = "command.prepare"
                Input = draft
                Cancellable = false
            }
            {
                Identifier = "command.execute"
                Input = draft
                Cancellable = false
            }
        ]

    let private caseReferenceInput target =
        Schema.objectOf
            false
            [ Schema.property "caseReference" (ScalarSchemas.scalar target.Scalar) true ]

    let private historyInput target maximumPageSize =
        Schema.objectOf
            false
            [
                Schema.property "caseReference" (ScalarSchemas.scalar target.Scalar) true
                Schema.property "cursor" (Schema.string None None (Some 1) None) false
                Schema.property
                    "limit"
                    (Schema.integer (Some 1L) (Some(int64 maximumPageSize)))
                    true
                Schema.property
                    "detail"
                    (Schema.enumeration [ TextConstant "SUMMARY"; TextConstant "FULL" ])
                    true
            ]

    let private operationInput =
        Schema.objectOf false [ Schema.property "operationId" ScalarSchemas.uuid true ]

    let private caseEndpoints target cursor maximumPageSize =
        [
            {
                Identifier = "case.get"
                Input = caseReferenceInput target
                Cancellable = true
            }
            {
                Identifier = "case.list"
                Input = cursor
                Cancellable = true
            }
            {
                Identifier = "case.history"
                Input = historyInput target maximumPageSize
                Cancellable = true
            }
            {
                Identifier = "operation.observe"
                Input = operationInput
                Cancellable = true
            }
            {
                Identifier = "recovery.list"
                Input = cursor
                Cancellable = true
            }
            {
                Identifier = "recovery.inspect"
                Input = operationInput
                Cancellable = true
            }
        ]

    let private recoveryEndpoints =
        [
            {
                Identifier = "recovery.resolve"
                Input = EndpointInputs.recoveryResolution
                Cancellable = false
            }
            {
                Identifier = "recovery.dismiss"
                Input = EndpointInputs.recoveryDismiss
                Cancellable = false
            }
            {
                Identifier = "recovery.export"
                Input = EndpointInputs.recoveryExport
                Cancellable = true
            }
            {
                Identifier = "recovery.importEnvelopePreview"
                Input = EndpointInputs.importPreview
                Cancellable = true
            }
            {
                Identifier = "recovery.importEnvelopeRetain"
                Input = EndpointInputs.importRetain
                Cancellable = false
            }
            {
                Identifier = "recovery.importRecordPreview"
                Input = EndpointInputs.importPreview
                Cancellable = true
            }
            {
                Identifier = "recovery.importRecordRetain"
                Input = EndpointInputs.importRetain
                Cancellable = false
            }
        ]

    let all (semantic: SemanticCoreContract) =
        let cursor = EndpointInputs.cursor semantic.MaximumPageSize
        let commands = ProjectionSchema.commandDraft semantic |> commandEndpoints
        let cases = caseEndpoints (caseReference semantic) cursor semantic.MaximumPageSize
        commands @ cases @ recoveryEndpoints
