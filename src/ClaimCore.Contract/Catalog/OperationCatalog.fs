namespace ClaimCore.Contract

open ClaimCore.Contract.Protocol

open ClaimCore.Domain

open ClaimCore.Json

open System
open System.Text.RegularExpressions

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

module TechnicalInputs =
    let vocabulary =
        Vocabulary.ofUnion<TechnicalInput> "input" TokenCasing.CamelCase None

    let token = Vocabulary.token vocabulary

[<RequireQualifiedAccess>]
type CatalogInputName =
    | Field of BusinessField
    | Group of RecordGroup
    | Technical of TechnicalInput

[<RequireQualifiedAccess>]
type CatalogArgument =
    | Named
    | Operand
    | Implicit

[<RequireQualifiedAccess>]
type CatalogAtom =
    | Text
    | Integer
    | Boolean
    | Choice of string list
    | Switch of off: string * on: string

/// Catalog identity is an endpoint and, for execution, one act selected from its codec.
type CatalogOperation =
    {
        Endpoint: string
        Act: ActKind option
    }

/// CLI argument presentation is derived alongside the published binding, never authored as a second shape.
type CatalogDerivedInput =
    {
        Name: CatalogInputName
        Atom: CatalogAtom
        Argument: CatalogArgument
        Binding: CatalogInput
    }

module OperationCatalog =
    let inputKey =
        function
        | CatalogInputName.Field field -> Record.key field
        | CatalogInputName.Group group -> RecordGroup.token group
        | CatalogInputName.Technical input -> TechnicalInputs.token input

    let inputMessage name part =
        match name with
        | CatalogInputName.Field field -> Vocabulary.messageKey RecordFields.vocabulary field part
        | CatalogInputName.Group group -> Vocabulary.messageKey RecordGroup.vocabulary group part
        | CatalogInputName.Technical input ->
            Vocabulary.messageKey TechnicalInputs.vocabulary input part

    [<NoEquality; NoComparison>]
    type private Declaration =
        | Case of CaseActDeclaration
        | Authority of AuthorityActDeclaration
        | Read of ReadDeclaration

    let private declarations =
        (Record.caseActs
         |> List.map (fun declaration ->
             {
                 Endpoint = Api.operationExecute.Token
                 Act = Some(ActKind.Case declaration.Kind)
             },
             Case declaration))
        @ (Record.authorityActs
           |> List.map (fun declaration ->
               {
                   Endpoint = Api.operationExecute.Token
                   Act = Some(ActKind.Authority declaration.Kind)
               },
               Authority declaration))
        @ (Record.reads
           |> List.map (fun declaration ->
               {
                   Endpoint = Vocabulary.token ActorEndpoints.vocabulary declaration.Endpoint
                   Act = None
               },
               Read declaration))

    let all = declarations |> List.map fst

    let mutations = all |> List.filter (fun operation -> operation.Act.IsSome)

    let authorityKinds =
        mutations
        |> List.choose (fun operation ->
            match operation.Act with
            | Some(ActKind.Authority kind) -> Some kind
            | _ -> None)

    let private message entry part =
        match entry.Act with
        | Some act -> Vocabulary.messageKey ActKinds.vocabulary act part
        | None -> "endpoint." + entry.Endpoint + "." + part

    let labelId entry = message entry "label"
    let meaningId entry = message entry "meaning"

    let confirmationId entry =
        entry.Act |> Option.map (fun _ -> message entry "confirmation")

    let kebab (value: string) =
        Regex.Replace(value, "([a-z0-9])([A-Z])", "$1-$2").Replace('_', '-').ToLowerInvariant()

    let words entry =
        match entry.Act with
        | Some(ActKind.Case kind) -> [ "case"; kebab (CaseActKinds.token kind) ]
        | Some(ActKind.Authority kind) -> [ "authority"; kebab (AuthorityKinds.token kind) ]
        | None -> entry.Endpoint.Split('.') |> List.ofArray

    let private source entry name =
        match name with
        | CatalogInputName.Technical TechnicalInput.OperationId when entry.Act.IsSome ->
            InputSource.OperationIdentity
        | CatalogInputName.Technical TechnicalInput.ExpectedRevision -> InputSource.ExpectedRevision
        | _ -> InputSource.Authored

    let private targetFor =
        function
        | CatalogInputName.Field field -> Some(InputTarget.Field field)
        | CatalogInputName.Technical TechnicalInput.Reason -> Some InputTarget.Reason
        | _ -> None

    let private diagnostics =
        function
        | CatalogInputName.Field field -> [ Record.key field ]
        | CatalogInputName.Group group -> [ RecordGroup.token group ]
        | CatalogInputName.Technical TechnicalInput.OperationId -> [ "operationId" ]
        | CatalogInputName.Technical TechnicalInput.ExpectedRevision -> [ "expectedRevision" ]
        | CatalogInputName.Technical TechnicalInput.Issuer -> [ "principal.issuer" ]
        | CatalogInputName.Technical TechnicalInput.Subject -> [ "principal.subject" ]
        | CatalogInputName.Technical TechnicalInput.Reason -> [ "reason" ]
        | _ -> []


    let private actPath tail = "act" :: tail

    let private input operation name path kind argument branches required defaultValue prefill =
        let atom =
            match kind with
            | BindingKind.Text -> CatalogAtom.Text
            | BindingKind.Integer -> CatalogAtom.Integer
            | BindingKind.Boolean -> CatalogAtom.Boolean
            | BindingKind.Choice values
            | BindingKind.Group values -> CatalogAtom.Choice values

        {
            Name = name
            Atom = atom
            Argument = argument
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

    let private named operation technical path kind required =
        input
            operation
            (CatalogInputName.Technical technical)
            path
            kind
            CatalogArgument.Named
            []
            required
            None
            None

    let private caseOperand operation =
        let path = [ Record.key BusinessField.CaseReference ]

        input
            operation
            (CatalogInputName.Field BusinessField.CaseReference)
            path
            BindingKind.Text
            CatalogArgument.Operand
            []
            true
            None
            (Some path)

    let private operationIdentity operation =
        named operation TechnicalInput.OperationId [ "operationId" ] BindingKind.Text false

    let private values operation group prefix branches prefill =
        Record.fieldsOf group
        |> List.map (fun field ->
            input
                operation
                (CatalogInputName.Field field)
                (prefix @ [ Record.key field ])
                BindingKind.Text
                CatalogArgument.Named
                branches
                true
                None
                (if prefill then
                     Some [ RecordGroup.token group; Record.key field ]
                 else
                     None))

    let private correction operation =
        Vocabulary.all RecordGroup.vocabulary
        |> List.collect (fun group ->
            let prefix = actPath ("groups" :: [ RecordGroup.token group ])
            let tag = prefix @ [ "kind" ]
            let choices = CorrectionActions.forGroup group |> List.map CorrectionActions.token

            let selector =
                input
                    operation
                    (CatalogInputName.Group group)
                    tag
                    (BindingKind.Group choices)
                    CatalogArgument.Named
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
                        Value = CorrectionActions.token CorrectionGroupAction.Replace
                    }
                ]
                true)

    let caseInputs operation (declaration: CaseActDeclaration) =
        let payload =
            match declaration.Payload with
            | CasePayload.NoPayload _ -> []
            | CasePayload.Registration shape ->
                values operation RecordGroup.Registration (actPath [ "values" ]) [] shape.Prefill
            | CasePayload.Decision shape ->
                values operation RecordGroup.Decision (actPath [ "values" ]) [] shape.Prefill
            | CasePayload.Payment shape ->
                values operation RecordGroup.Payment (actPath [ "values" ]) [] shape.Prefill
            | CasePayload.Reason _ ->
                [
                    named
                        operation
                        TechnicalInput.Reason
                        (actPath [ "reason" ])
                        BindingKind.Text
                        true
                ]
            | CasePayload.Correction _ -> correction operation

        let revision =
            match declaration.RevisionMode with
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
                        Value = BindingValue.Text OperationSubjects.Case
                        Branches = []
                    }
                match declaration.RevisionMode with
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
                        Value = BindingValue.Text(CaseActKinds.token declaration.Kind)
                        Branches = []
                    }
            ]

        operationIdentity operation :: caseOperand operation :: (revision @ payload), constants

    let private authorityInputs operation (declaration: AuthorityActDeclaration) =
        let target =
            [
                named
                    operation
                    TechnicalInput.PrincipalKind
                    [ "target"; "kind" ]
                    (BindingKind.Choice PrincipalKinds.all)
                    true
                named operation TechnicalInput.Issuer [ "target"; "issuer" ] BindingKind.Text true
                named operation TechnicalInput.Subject [ "target"; "subject" ] BindingKind.Text true
            ]

        let grant =
            match declaration.Payload with
            | AuthorityPayload.Target _ -> []
            | AuthorityPayload.Grant _ ->
                let scope = actPath ("scope" :: [ "kind" ])

                [
                    named
                        operation
                        TechnicalInput.Role
                        (actPath [ "role" ])
                        (BindingKind.Choice(RoleTokens.all |> List.map RoleTokens.token))
                        true
                    input
                        operation
                        (CatalogInputName.Technical TechnicalInput.Scope)
                        scope
                        (BindingKind.Group [ "INSTALLATION"; "CASE" ])
                        CatalogArgument.Implicit
                        []
                        false
                        None
                        None
                    input
                        operation
                        (CatalogInputName.Field BusinessField.CaseReference)
                        (actPath ("scope" :: [ Record.key BusinessField.CaseReference ]))
                        BindingKind.Text
                        CatalogArgument.Named
                        [ { Path = scope; Value = "CASE" } ]
                        true
                        None
                        None
                ]

        let constants =
            [
                {
                    Path = [ "kind" ]
                    Value = BindingValue.Text OperationSubjects.Authority
                    Branches = []
                }
                {
                    Path = actPath [ "kind" ]
                    Value = BindingValue.Text(AuthorityKinds.token declaration.Kind)
                    Branches = []
                }
            ]

        operationIdentity operation :: (target @ grant), constants

    let private readInputs operation (declaration: ReadDeclaration) =
        let operand technical path =
            input
                operation
                (CatalogInputName.Technical technical)
                path
                BindingKind.Text
                CatalogArgument.Operand
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
                    Vocabulary.token HistoryDetails.vocabulary HistoryDetail.Summary,
                    Vocabulary.token HistoryDetails.vocabulary HistoryDetail.Full

                let binding =
                    named
                        operation
                        TechnicalInput.Full
                        [ "detail" ]
                        (BindingKind.Choice [ off; on ])
                        false

                [
                    { binding with
                        Atom = CatalogAtom.Switch(off, on)
                    }
                ]
            | ReadInput.Page ->
                [
                    named operation TechnicalInput.Cursor [ "cursor" ] BindingKind.Text false
                    input
                        operation
                        (CatalogInputName.Technical TechnicalInput.Limit)
                        [ "limit" ]
                        BindingKind.Integer
                        CatalogArgument.Named
                        []
                        false
                        (Some(BindingValue.Integer(int64 Limits.DefaultPageSize)))
                        None
                ]),
        []

    let private construct operation declaration =
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

    let derive operation = Map.find operation derived

    let synopsis operation =
        let placeholder =
            function
            | CatalogAtom.Text -> "VALUE"
            | CatalogAtom.Integer -> "N"
            | CatalogAtom.Boolean -> "true|false"
            | CatalogAtom.Choice choices -> choices |> List.map kebab |> String.concat "|"
            | CatalogAtom.Switch _ -> ""

        let inputs, _ = derive operation

        let arguments =
            inputs
            |> List.choose (fun input ->
                match input.Argument with
                | CatalogArgument.Implicit -> None
                | CatalogArgument.Operand ->
                    Some(kebab (inputKey input.Name) |> _.ToUpperInvariant())
                | CatalogArgument.Named ->
                    let suffix = placeholder input.Atom

                    let text =
                        "--"
                        + kebab (inputKey input.Name)
                        + (if suffix = "" then "" else " " + suffix)

                    Some(
                        if input.Binding.Required && input.Binding.Branches.IsEmpty then
                            text
                        else
                            "[" + text + "]"
                    ))

        String.concat " " (words operation @ arguments)

    let entries =
        all
        |> List.map (fun operation ->
            let inputs, constants = derive operation

            {
                Endpoint = operation.Endpoint
                LabelId = labelId operation
                MeaningId = meaningId operation
                ConfirmationId = confirmationId operation
                Synopsis = synopsis operation
                Subject = words operation |> List.head |> _.ToUpperInvariant()
                Kind =
                    operation.Act
                    |> Option.map ActKinds.token
                    |> Option.defaultValue operation.Endpoint
                Inputs = inputs |> List.map _.Binding
                Constants = constants
            })

    let operation (entry: CatalogEntry) =
        {
            Endpoint = entry.Endpoint
            Act =
                if entry.Endpoint = Api.operationExecute.Token then
                    Some(
                        Vocabulary.tryParse ActKinds.vocabulary entry.Kind
                        |> Option.defaultWith (fun () ->
                            invalidArg (nameof entry) "Unknown catalog act.")
                    )
                else
                    None
        }
