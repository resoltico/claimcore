namespace ClaimCore.Protocol

open ClaimCore.Protocol

open ClaimCore.Domain

open ClaimCore.Json

open System

/// Typed semantic identity accompanies its published binding; renderers choose their grammar.
type OperationInputDefinition =
    {
        Name: InteractionInput
        Binding: CatalogInput
    }

module OperationCatalog =
    let inputKey = InteractionInput.key
    let inputMessage = InteractionInput.messageKey

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
        @ (ReadCatalog.declarations
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
        | None -> Vocabulary.messageKey ActorEndpoint.vocabulary entry.Endpoint part

    let labelId (entry: CatalogOperation) = message entry "label"
    let meaningId (entry: CatalogOperation) = message entry "meaning"

    let confirmationId (entry: CatalogOperation) =
        entry.SelectedAct |> Option.map (fun _ -> message entry "confirmation")

    let private source (entry: CatalogOperation) name =
        match name with
        | (InteractionInput.Target InputTarget.OperationId) when entry.SelectedAct.IsSome ->
            InputSource.OperationIdentity
        | (InteractionInput.Target InputTarget.ExpectedRevision) -> InputSource.ExpectedRevision
        | _ -> InputSource.Authored

    let private targetFor =
        function
        | InteractionInput.Target(InputTarget.Field field) -> Some(InputTarget.Field field)
        | (InteractionInput.Target InputTarget.Reason) -> Some InputTarget.Reason
        | (InteractionInput.Target InputTarget.Role) -> Some InputTarget.Role
        | _ -> None

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
                    DiagnosticTargets =
                        match name with
                        | InteractionInput.Target target -> [ InputTarget.token target ]
                        | _ -> []
                    Source = source operation name
                }
        }

    let private named (operation: CatalogOperation) technical path kind required =
        input operation technical path kind [] required None None

    let private caseOperand (operation: CatalogOperation) =
        let path = [ BusinessField.key BusinessField.CaseReference ]

        input
            operation
            (InteractionInput.Target(InputTarget.Field BusinessField.CaseReference))
            path
            BindingKind.Text
            []
            true
            None
            (Some path)

    let private operationIdentity (operation: CatalogOperation) =
        named
            operation
            (InteractionInput.Target InputTarget.OperationId)
            [ "operationId" ]
            BindingKind.Text
            false

    let private values (operation: CatalogOperation) group prefix branches prefill =
        CaseFields.ofGroup group
        |> List.map (fun field ->
            input
                operation
                (InteractionInput.Target(InputTarget.Field field))
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
                (match group with
                 | RecordGroup.Registration -> Amendment.actions
                 | RecordGroup.Decision
                 | RecordGroup.Payment -> CorrectionGroupAction.all)
                |> List.map CorrectionGroupAction.token

            let selector =
                input
                    operation
                    (InteractionInput.Target(InputTarget.Group group))
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
            match (CaseAct.slot declaration: CaseActSlot<unit, unit, unit, unit>) with
            | CaseActSlot.Bare _ -> []
            | CaseActSlot.Registration _ ->
                values
                    operation
                    RecordGroup.Registration
                    (actPath [ "values" ])
                    []
                    (CaseActAnnotations.prefill declaration)
            | CaseActSlot.Decision _ ->
                values
                    operation
                    RecordGroup.Decision
                    (actPath [ "values" ])
                    []
                    (CaseActAnnotations.prefill declaration)
            | CaseActSlot.Payment _ ->
                values
                    operation
                    RecordGroup.Payment
                    (actPath [ "values" ])
                    []
                    (CaseActAnnotations.prefill declaration)
            | CaseActSlot.Reason _ ->
                [
                    named
                        operation
                        (InteractionInput.Target InputTarget.Reason)
                        (actPath [ "reason" ])
                        BindingKind.Text
                        true
                ]
            | CaseActSlot.Correction _ -> correction operation

        let revision =
            match (CaseActAnnotations.revision declaration) with
            | RevisionInput.New -> []
            | RevisionInput.Expected ->
                [
                    named
                        operation
                        (InteractionInput.Target InputTarget.ExpectedRevision)
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
                match (CaseActAnnotations.revision declaration) with
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
                    (InteractionInput.Target InputTarget.PrincipalKind)
                    [ "target"; "kind" ]
                    (BindingKind.Choice PrincipalKind.all)
                    true
                named
                    operation
                    (InteractionInput.Target InputTarget.PrincipalIssuer)
                    [ "target"; "issuer" ]
                    BindingKind.Text
                    true
                named
                    operation
                    (InteractionInput.Target InputTarget.PrincipalSubject)
                    [ "target"; "subject" ]
                    BindingKind.Text
                    true
            ]

        let grant =
            match (AuthorityAct.slot declaration: AuthorityActSlot<unit>) with
            | AuthorityActSlot.Bare _ -> []
            | AuthorityActSlot.Grant _ ->
                let scope = actPath ("scope" :: [ "kind" ])

                [
                    named
                        operation
                        (InteractionInput.Target InputTarget.Role)
                        (actPath [ "role" ])
                        (BindingKind.Choice(Role.all |> List.map Role.token))
                        true
                    input
                        operation
                        (InteractionInput.Scope)
                        scope
                        (BindingKind.Group(ScopeKind.all |> List.map ScopeKind.token))
                        []
                        false
                        None
                        None
                    input
                        operation
                        (InteractionInput.Target(InputTarget.Field BusinessField.CaseReference))
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
            input operation technical path BindingKind.Text [] true None None

        declaration.Inputs
        |> List.collect (function
            | ReadInput.CaseOperand -> [ caseOperand operation ]
            | ReadInput.OperationOperand ->
                [ operand (InteractionInput.Target InputTarget.OperationId) [ "operationId" ] ]
            | ReadInput.ActorOperand -> [ operand InteractionInput.ActorId [ "actorId" ] ]
            | ReadInput.Detail ->
                let off, on =
                    Vocabulary.token HistoryDetail.vocabulary HistoryDetail.Summary,
                    Vocabulary.token HistoryDetail.vocabulary HistoryDetail.Full

                let binding =
                    named
                        operation
                        InteractionInput.Detail
                        [ "detail" ]
                        (BindingKind.Choice [ off; on ])
                        false

                [ binding ]
            | ReadInput.Page ->
                [
                    named
                        operation
                        (InteractionInput.Target InputTarget.Cursor)
                        [ "cursor" ]
                        BindingKind.Text
                        false
                    input
                        operation
                        ((InteractionInput.Target InputTarget.Limit))
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
