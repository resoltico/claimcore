namespace ClaimCore.ContractGeneration

open ClaimCore.Contract
open ClaimCore.Domain
open ClaimCore.Application

/// Presentation names for protocol structure, separate from the business fields.
module internal ProtocolFieldPresentation =
    let authorityFields
        (target: AuthorityInputDefinition)
        (acts: AuthorityOperationDefinition list)
        =
        [
            for name in [ "operationId"; "expectedRevision"; "act"; "kind"; "issuer"; "subject" ] do
                yield name, AuthorityOperationDefinitions.labelId name
            yield target.Name, target.LabelId
            for definition in acts do
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
        Acts: ActDefinition list
        AuthorityTarget: AuthorityInputDefinition
        AuthorityActs: AuthorityOperationDefinition list
    }

/// Domain and Application own metadata values; Contract owns their public encoding.
module ContractCatalog =
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
            Acts = ActPresentation.all
            AuthorityTarget = AuthorityOperationDefinitions.target
            AuthorityActs = AuthorityOperationDefinitions.all
        }

    let valueFrom definitions : CatalogDocument =
        {
            Fields = definitions.Fields
            Acts = definitions.Acts
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
                        Scalar = ScalarRule.Text(FieldDefinitions.text TextInput.Reason)
                    }
                ]
            AuthorityTarget = definitions.AuthorityTarget
            AuthorityActs = definitions.AuthorityActs
            AuthorityFields =
                ProtocolFieldPresentation.authorityFields
                    definitions.AuthorityTarget
                    definitions.AuthorityActs
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
            Entries = CatalogEntries.all
            ProductVersion = BuildIdentity.current.Version
        }

    let renderFrom definitions =
        valueFrom definitions
        |> CatalogCodecs.codec.Write
        |> WireWriter.indentedBytes
        |> fun bytes -> Array.append bytes [| byte '\n' |]

    let render () = renderFrom defaults
