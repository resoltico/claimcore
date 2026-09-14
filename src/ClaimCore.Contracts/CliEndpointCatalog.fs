namespace ClaimCore.Contracts

open ClaimCore.Application

module internal CliEndpointCatalog =
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

    let private caseEndpoints (semantic: SemanticCoreContract) =
        let cursor = EndpointInputs.cursor semantic.MaximumPageSize

        [
            {
                Identifier = "case.get"
                Input = EndpointInputs.caseReference semantic
                Cancellable = true
            }
            {
                Identifier = "case.list"
                Input = cursor
                Cancellable = true
            }
            {
                Identifier = "case.history"
                Input = EndpointInputs.history semantic
                Cancellable = true
            }
            {
                Identifier = "operation.observe"
                Input = EndpointInputs.operation
                Cancellable = true
            }
            {
                Identifier = "recovery.list"
                Input = cursor
                Cancellable = true
            }
            {
                Identifier = "recovery.inspect"
                Input = EndpointInputs.operation
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
        let commands = ProjectionSchema.commandDraft semantic |> commandEndpoints
        let cases = caseEndpoints semantic
        commands @ cases @ recoveryEndpoints
