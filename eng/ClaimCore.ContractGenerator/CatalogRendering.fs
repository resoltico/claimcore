namespace ClaimCore.ContractGeneration

open ClaimCore.Contract
open ClaimCore.Domain
open ClaimCore.Application

type PresentationDefinitions =
    {
        Fields: FieldPresentationDefinition list
        Acts: ActDefinition list
        AuthorityTarget: AuthorityInputDefinition
        AuthorityActs: AuthorityOperationDefinition list
    }

/// Serialize Contract catalog projections with Domain scalar rules and Application capabilities.
module ContractCatalog =
    let defaults =
        {
            Fields =
                FieldDefinitions.all
                |> List.map (fun field ->
                    {
                        Name = field.Field
                        Group = (FieldTable.forField field.Field).Group
                        LabelId = FieldTable.labelId field.Field
                        MeaningId = FieldTable.meaningId field.Field
                        Scalar = field.Scalar
                        AllowsAbsence = (FieldTable.forField field.Field).Optional
                    })
            Acts = OperationCatalog.caseDefinitions
            AuthorityTarget = OperationCatalog.authorityTarget
            AuthorityActs = OperationCatalog.authorityDefinitions
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
                            OperationCatalog.inputMessage
                                (CatalogInputName.Technical TechnicalInput.Reason)
                                "label"
                        MeaningId =
                            OperationCatalog.inputMessage
                                (CatalogInputName.Technical TechnicalInput.Reason)
                                "meaning"
                        Scalar = ScalarRule.Text(FieldDefinitions.text TextInput.Reason)
                    }
                ]
            AuthorityTarget = definitions.AuthorityTarget
            AuthorityActs = definitions.AuthorityActs
            AuthorityFields = OperationCatalog.technicalFields
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
            Entries = OperationCatalog.entries
            ProductVersion = BuildIdentity.current.Version
        }

    let renderFrom definitions =
        valueFrom definitions
        |> CatalogCodecs.codec.Write
        |> WireWriter.indentedBytes
        |> fun bytes -> Array.append bytes [| byte '\n' |]

    let render () = renderFrom defaults
