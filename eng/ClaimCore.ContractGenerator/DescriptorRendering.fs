namespace ClaimCore.ContractGeneration

open ClaimCore.Contract
open ClaimCore.Domain
open ClaimCore.Application

/// Presentation names for protocol structure, separate from the business fields.
module internal ProtocolFieldPresentation =
    let authorityFields
        (target: AuthorityInputDefinition)
        (commands: AuthorityOperationDefinition list)
        =
        [
            for name in [ "operationId"; "expectedRevision"; "act"; "kind"; "issuer"; "subject" ] do
                yield name, AuthorityOperationDefinitions.labelId name
            yield target.Name, target.LabelId
            for definition in commands do
                for input in definition.Inputs do
                    yield input.Name, input.LabelId
        ]
        |> List.distinct
        |> List.groupBy fst
        |> List.map (fun (_, values) ->
            match values with
            | [ name, identity ] -> name, identity
            | _ -> invalidOp "Protocol field presentation identities disagree.")

type PresentationDefinitions =
    {
        Fields: FieldPresentationDefinition list
        Commands: CommandDefinition list
        AuthorityTarget: AuthorityInputDefinition
        AuthorityCommands: AuthorityOperationDefinition list
    }

/// Domain and Application own metadata values; Contract owns their public encoding.
module ContractDescriptors =
    let defaults =
        {
            Fields =
                FieldDefinitions.all
                |> List.map (fun field ->
                    {
                        Name = field.Field
                        LabelId = FieldTable.labelId field.Field
                        MeaningId = FieldTable.meaningId field.Field
                        Scalar = field.Scalar
                        AllowsAbsence = (FieldTable.forField field.Field).Optional
                    })
            Commands = CommandPresentation.all
            AuthorityTarget = AuthorityOperationDefinitions.target
            AuthorityCommands = AuthorityOperationDefinitions.all
        }

    let valueFrom definitions : PresentationDescriptors =
        {
            Fields = definitions.Fields
            Commands = definitions.Commands
            InputFields =
                [
                    {
                        Name = InputTarget.Reason
                        LabelId =
                            Vocabulary.messageKey InputTargets.vocabulary InputTarget.Reason "label"
                        MeaningId =
                            Vocabulary.messageKey
                                InputTargets.vocabulary
                                InputTarget.Reason
                                "meaning"
                        Scalar = FieldDefinitions.scalar InputTarget.Reason
                    }
                ]
            AuthorityTarget = definitions.AuthorityTarget
            AuthorityCommands = definitions.AuthorityCommands
            AuthorityFields =
                ProtocolFieldPresentation.authorityFields
                    definitions.AuthorityTarget
                    definitions.AuthorityCommands
                |> List.map (fun (name, label) -> { Name = name; LabelId = label })
            CharacterWarnings =
                {
                    MaximumCodePoints = TextDisplay.maximumCodePoints
                    Categories = TextDisplay.categoryTokens
                }
            Statuses = CaseStatuses.all
            RejectionDiagnostics = Refusals.definitions
            FaultDiagnostics = CoreFaults.definitions
            Roles =
                RoleTokens.all
                |> List.map (fun role ->
                    {
                        Role = role
                        Capabilities = ActorAuthorization.capabilities role
                    })
            Limits =
                {
                    DefaultPageSize = ServiceLimits.DefaultPageSize
                    MaximumPageSize = ServiceLimits.MaximumPageSize
                    RequestByteLimit = ServiceLimits.RequestByteLimit
                }
            Interactions = InteractionDefinitions.all
            ProductVersion = BuildIdentity.current.Version
        }

    let renderFrom definitions =
        valueFrom definitions
        |> DescriptorCodecs.codec.Write
        |> WireWriter.indentedBytes
        |> fun bytes -> Array.append bytes [| byte '\n' |]

    let render () = renderFrom defaults
