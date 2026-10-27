namespace ClaimCore.Protocol

open ClaimCore.Protocol

open ClaimCore.Domain

open ClaimCore.Json

open System

[<RequireQualifiedAccess>]
type TechnicalInput =
    | OperationId
    | ExpectedRevision
    | PrincipalKind
    | Issuer
    | Subject
    | Role
    | Scope
    | Target
    | Act
    | ActorId
    | Cursor
    | Limit
    | Full
    | Reason

[<CompilationRepresentation(CompilationRepresentationFlags.ModuleSuffix)>]
module TechnicalInput =
    let vocabulary =
        Vocabulary.ofUnion<TechnicalInput> "input" TokenCasing.CamelCase None

    let token = Vocabulary.token vocabulary

[<RequireQualifiedAccess>]
type CatalogInputName =
    | Field of BusinessField
    | Group of RecordGroup
    | Technical of TechnicalInput

/// Typed semantic identity accompanies its published binding; renderers choose their grammar.
type OperationInputDefinition =
    {
        Name: CatalogInputName
        Binding: CatalogInput
    }

module OperationCatalog =
    let inputKey =
        function
        | CatalogInputName.Field field -> BusinessField.key field
        | CatalogInputName.Group group -> RecordGroup.token group
        | CatalogInputName.Technical input -> TechnicalInput.token input

    let inputMessage name part =
        match name with
        | CatalogInputName.Field field -> Vocabulary.messageKey BusinessField.vocabulary field part
        | CatalogInputName.Group group -> Vocabulary.messageKey RecordGroup.vocabulary group part
        | CatalogInputName.Technical input ->
            Vocabulary.messageKey TechnicalInput.vocabulary input part

    [<NoEquality; NoComparison>]
    type private Declaration =
        | Case of CaseActKind
        | Authority of AuthorityKind
        | Read of ReadDeclaration

    let private declarations =
        (CaseActKind.all
         |> List.map (fun kind -> CatalogOperation.Act(ActKind.Case kind), Case kind))
        @ (AuthorityKind.all
           |> List.filter ((<>) AuthorityKind.ProvisionOwner)
           |> List.map (fun kind -> CatalogOperation.Act(ActKind.Authority kind), Authority kind))
        @ (Record.reads
           |> List.map (fun declaration ->
               CatalogOperation.Query declaration.Endpoint, Read declaration))

    let all = declarations |> List.map fst

    let mutations = all |> List.filter (fun operation -> operation.SelectedAct.IsSome)

    let authorityKinds =
        mutations
        |> List.choose (fun operation ->
            match operation.SelectedAct with
            | Some(ActKind.Authority kind) -> Some kind
            | _ -> None)

    let private message (entry: CatalogOperation) part =
        match entry.SelectedAct with
        | Some act -> Vocabulary.messageKey ActKind.vocabulary act part
        | None -> "endpoint." + ActorEndpoint.token entry.Endpoint + "." + part

    let labelId (entry: CatalogOperation) = message entry "label"
    let meaningId (entry: CatalogOperation) = message entry "meaning"

    let confirmationId (entry: CatalogOperation) =
        entry.SelectedAct |> Option.map (fun _ -> message entry "confirmation")

    let private source (entry: CatalogOperation) name =
        match name with
        | CatalogInputName.Technical TechnicalInput.OperationId when entry.SelectedAct.IsSome ->
            InputSource.OperationIdentity
        | CatalogInputName.Technical TechnicalInput.ExpectedRevision -> InputSource.ExpectedRevision
        | _ -> InputSource.Authored

    let private targetFor =
        function
        | CatalogInputName.Field field -> Some(InputTarget.Field field)
        | CatalogInputName.Technical TechnicalInput.Reason -> Some InputTarget.Reason
        | CatalogInputName.Technical TechnicalInput.Role -> Some InputTarget.Role
        | _ -> None

    let private diagnostics =
        function
        | CatalogInputName.Field field -> [ BusinessField.key field ]
        | CatalogInputName.Group group -> [ RecordGroup.token group ]
        | CatalogInputName.Technical TechnicalInput.OperationId -> [ "operationId" ]
        | CatalogInputName.Technical TechnicalInput.ExpectedRevision -> [ "expectedRevision" ]
        | CatalogInputName.Technical TechnicalInput.Issuer -> [ "principal.issuer" ]
        | CatalogInputName.Technical TechnicalInput.Subject -> [ "principal.subject" ]
        | CatalogInputName.Technical TechnicalInput.Reason -> [ "reason" ]
        | CatalogInputName.Technical TechnicalInput.Role -> [ "role" ]
        | _ -> []


    let private actPath tail = "act" :: tail

    let private input
        (operation: CatalogOperation)
        name
        path
        kind
        branches
        required
        defaultValue
        prefill
        =
        {
            Name = name
            Binding =
                {
                    Identity = String.concat "." path
                    Field = targetFor name
                    Path = path
                    Kind = kind
                    Branches = branches
                    LabelId = inputMessage name "label"
                    MeaningId = Some(inputMessage name "meaning")
                    Order = 0
                    Prefill = prefill
                    Required = required
                    Default = defaultValue
                    DiagnosticTargets = diagnostics name
                    Source = source operation name
                }
        }

    let private named (operation: CatalogOperation) technical path kind required =
        input operation (CatalogInputName.Technical technical) path kind [] required None None

    let private caseOperand (operation: CatalogOperation) =
        let path = [ BusinessField.key BusinessField.CaseReference ]

        input
            operation
            (CatalogInputName.Field BusinessField.CaseReference)
            path
            BindingKind.Text
            []
            true
            None
            (Some path)

    let private operationIdentity (operation: CatalogOperation) =
        named operation TechnicalInput.OperationId [ "operationId" ] BindingKind.Text false

    let private values (operation: CatalogOperation) group prefix branches prefill =
        Record.fieldsOf group
        |> List.map (fun field ->
            input
                operation
                (CatalogInputName.Field field)
                (prefix @ [ BusinessField.key field ])
                BindingKind.Text
                branches
                true
                None
                (if prefill then
                     Some [ RecordGroup.token group; BusinessField.key field ]
                 else
                     None))

    let private correction (operation: CatalogOperation) =
        Vocabulary.all RecordGroup.vocabulary
        |> List.collect (fun group ->
            let prefix = actPath ("groups" :: [ RecordGroup.token group ])
            let tag = prefix @ [ "kind" ]

            let choices =
                CorrectionGroupAction.forGroup group |> List.map CorrectionGroupAction.token

            let selector =
                input
                    operation
                    (CatalogInputName.Group group)
                    tag
                    (BindingKind.Group choices)
                    []
                    true
                    None
                    None

            selector
            :: values
                operation
                group
                (prefix @ [ "values" ])
                [
                    {
                        Path = tag
                        Value = CorrectionGroupAction.token CorrectionGroupAction.Replace
                    }
                ]
                true)

    let caseInputs (operation: CatalogOperation) (declaration: CaseActKind) =
        let payload =
            match CaseActKind.shape declaration with
            | ActShape.NoValues -> []
            | ActShape.RegistrationValues ->
                values
                    operation
                    RecordGroup.Registration
                    (actPath [ "values" ])
                    []
                    (CaseActKind.prefill declaration)
            | ActShape.DecisionValues ->
                values
                    operation
                    RecordGroup.Decision
                    (actPath [ "values" ])
                    []
                    (CaseActKind.prefill declaration)
            | ActShape.PaymentValues ->
                values
                    operation
                    RecordGroup.Payment
                    (actPath [ "values" ])
                    []
                    (CaseActKind.prefill declaration)
            | ActShape.ReasonText ->
                [
                    named
                        operation
                        TechnicalInput.Reason
                        (actPath [ "reason" ])
                        BindingKind.Text
                        true
                ]
            | ActShape.CorrectionGroups -> correction operation

        let revision =
            match (CaseActKind.revision declaration) with
            | RevisionInput.New -> []
            | RevisionInput.Expected ->
                [
                    named
                        operation
                        TechnicalInput.ExpectedRevision
                        [ "expectedRevision" ]
                        BindingKind.Integer
                        true
                ]

        let constants =
            [
                yield
                    {
                        Path = [ "kind" ]
                        Value = BindingValue.Text(ActSubject.token ActSubject.Case)
                        Branches = []
                    }
                match (CaseActKind.revision declaration) with
                | RevisionInput.New ->
                    yield
                        {
                            Path = [ "expectedRevision" ]
                            Value = BindingValue.Integer 0L
                            Branches = []
                        }
                | RevisionInput.Expected -> ()
                yield
                    {
                        Path = actPath [ "kind" ]
                        Value = BindingValue.Text(CaseActKind.token declaration)
                        Branches = []
                    }
            ]

        operationIdentity operation :: caseOperand operation :: (revision @ payload), constants

    let private authorityInputs (operation: CatalogOperation) (declaration: AuthorityKind) =
        let target =
            [
                named
                    operation
                    TechnicalInput.PrincipalKind
                    [ "target"; "kind" ]
                    (BindingKind.Choice PrincipalKind.all)
                    true
                named operation TechnicalInput.Issuer [ "target"; "issuer" ] BindingKind.Text true
                named operation TechnicalInput.Subject [ "target"; "subject" ] BindingKind.Text true
            ]

        let grant =
            match AuthorityAct.shape declaration with
            | AuthorityShape.TargetOnly -> []
            | AuthorityShape.GrantValues ->
                let scope = actPath ("scope" :: [ "kind" ])

                [
                    named
                        operation
                        TechnicalInput.Role
                        (actPath [ "role" ])
                        (BindingKind.Choice(Role.all |> List.map Role.token))
                        true
                    input
                        operation
                        (CatalogInputName.Technical TechnicalInput.Scope)
                        scope
                        (BindingKind.Group [ "INSTALLATION"; "CASE" ])
                        []
                        false
                        None
                        None
                    input
                        operation
                        (CatalogInputName.Field BusinessField.CaseReference)
                        (actPath ("scope" :: [ BusinessField.key BusinessField.CaseReference ]))
                        BindingKind.Text
                        [ { Path = scope; Value = "CASE" } ]
                        true
                        None
                        None
                ]

        let constants =
            [
                {
                    Path = [ "kind" ]
                    Value = BindingValue.Text(ActSubject.token ActSubject.Authority)
                    Branches = []
                }
                {
                    Path = actPath [ "kind" ]
                    Value = BindingValue.Text(AuthorityKind.token declaration)
                    Branches = []
                }
            ]

        operationIdentity operation :: (target @ grant), constants

    let private readInputs (operation: CatalogOperation) (declaration: ReadDeclaration) =
        let operand technical path =
            input
                operation
                (CatalogInputName.Technical technical)
                path
                BindingKind.Text
                []
                true
                None
                None

        declaration.Inputs
        |> List.collect (function
            | ReadInput.CaseOperand -> [ caseOperand operation ]
            | ReadInput.OperationOperand -> [ operand TechnicalInput.OperationId [ "operationId" ] ]
            | ReadInput.ActorOperand -> [ operand TechnicalInput.ActorId [ "actorId" ] ]
            | ReadInput.Detail ->
                let off, on =
                    Vocabulary.token HistoryDetail.vocabulary HistoryDetail.Summary,
                    Vocabulary.token HistoryDetail.vocabulary HistoryDetail.Full

                let binding =
                    named
                        operation
                        TechnicalInput.Full
                        [ "detail" ]
                        (BindingKind.Choice [ off; on ])
                        false

                [ binding ]
            | ReadInput.Page ->
                [
                    named operation TechnicalInput.Cursor [ "cursor" ] BindingKind.Text false
                    input
                        operation
                        (CatalogInputName.Technical TechnicalInput.Limit)
                        [ "limit" ]
                        BindingKind.Integer
                        []
                        false
                        (Some(BindingValue.Integer(int64 Paging.DefaultPageSize)))
                        None
                ]),
        []

    let private construct (operation: CatalogOperation) declaration =
        let inputs, constants =
            match declaration with
            | Case declaration -> caseInputs operation declaration
            | Authority declaration -> authorityInputs operation declaration
            | Read declaration -> readInputs operation declaration

        inputs
        |> List.mapi (fun order input ->
            { input with
                Binding = { input.Binding with Order = order }
            }),
        constants

    let private derived =
        declarations
        |> List.map (fun (operation, declaration) -> operation, construct operation declaration)
        |> Map.ofList

    let derive (operation: CatalogOperation) = Map.find operation derived

    let entries =
        all
        |> List.map (fun operation ->
            let inputs, constants = derive operation

            {
                Kind = operation
                LabelId = labelId operation
                MeaningId = meaningId operation
                ConfirmationId = confirmationId operation
                Inputs = inputs |> List.map _.Binding
                Constants = constants
            })

    let operation (entry: CatalogEntry) = entry.Kind
