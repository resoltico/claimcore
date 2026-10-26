namespace ClaimCore.Contract

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
        Vocabulary.define
            "input"
            [
                TechnicalInput.OperationId, "operationId"
                TechnicalInput.ExpectedRevision, "expectedRevision"
                TechnicalInput.PrincipalKind, "kind"
                TechnicalInput.Issuer, "issuer"
                TechnicalInput.Subject, "subject"
                TechnicalInput.Role, "role"
                TechnicalInput.Scope, "scope"
                TechnicalInput.Target, "target"
                TechnicalInput.Act, "act"
                TechnicalInput.ActorId, "actorId"
                TechnicalInput.Cursor, "cursor"
                TechnicalInput.Limit, "limit"
                TechnicalInput.Full, "full"
                TechnicalInput.Reason, "reason"
            ]

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

type CatalogAnnotations =
    {
        Operands: string list list
        Prefill: string list list
        Fixed: (string list * BindingValue) list
        Presence: string list list
        Switches: (string list * (string * string)) list
        Defaults: (string list * BindingValue) list
        Minted: string list list
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
        | CatalogInputName.Field field -> FieldTable.key field
        | CatalogInputName.Group group -> RecordGroup.token group
        | CatalogInputName.Technical input -> TechnicalInputs.token input

    let inputMessage name part =
        match name with
        | CatalogInputName.Field field -> Vocabulary.messageKey FieldTable.vocabulary field part
        | CatalogInputName.Group group -> Vocabulary.messageKey RecordGroup.vocabulary group part
        | CatalogInputName.Technical input ->
            Vocabulary.messageKey TechnicalInputs.vocabulary input part

    let private actorActs =
        let codec = Api.operationExecute.Input

        let rec resolve =
            function
            | Schema.Ref name -> resolve codec.Definitions[name]
            | schema -> schema

        let variants schema =
            match resolve schema with
            | Schema.OneOf(_, Some(_, mapping)) ->
                mapping |> List.map (fun (token, name) -> token, codec.Definitions[name])
            | _ -> invalidOp "Operation input requires tagged subject and act codecs."

        variants codec.Schema
        |> List.collect (fun (_, subject) ->
            match resolve subject with
            | Schema.Object(properties, _) ->
                properties
                |> List.find (fst >> (=) "act")
                |> snd
                |> variants
                |> List.map (fun (token, _) ->
                    Vocabulary.tryParse ActKinds.vocabulary token
                    |> Option.defaultWith (fun () -> invalidOp ("Unknown request act: " + token)))
            | _ -> invalidOp "An operation subject must be an object.")

    let all =
        (actorActs
         |> List.map (fun act ->
             {
                 Endpoint = Api.operationExecute.Id
                 Act = Some act
             }))
        @ (Api.all
           |> List.map Api.id
           |> List.filter (fun id ->
               not (
                   List.contains
                       id
                       [
                           Api.session.Id
                           Api.logout.Id
                           Api.operationExecute.Id
                           Api.operationPreview.Id
                       ]
               ))
           |> List.map (fun endpoint -> { Endpoint = endpoint; Act = None }))

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
        | None -> "cli.read." + entry.Endpoint + "." + part

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
        | CatalogInputName.Field field -> [ FieldTable.key field ]
        | CatalogInputName.Group group -> [ RecordGroup.token group ]
        | CatalogInputName.Technical TechnicalInput.OperationId -> [ "operationId" ]
        | CatalogInputName.Technical TechnicalInput.ExpectedRevision -> [ "expectedRevision" ]
        | CatalogInputName.Technical TechnicalInput.Issuer -> [ "principal.issuer" ]
        | CatalogInputName.Technical TechnicalInput.Subject -> [ "principal.subject" ]
        | CatalogInputName.Technical TechnicalInput.Reason -> [ "reason" ]
        | _ -> []


    let annotations operation =
        let caseScoped =
            match operation.Act with
            | Some(ActKind.Case _) -> true
            | _ -> List.contains operation.Endpoint [ Api.caseGet.Id; Api.caseHistory.Id ]

        let paged =
            List.contains
                operation.Endpoint
                [
                    Api.caseList.Id
                    Api.caseHistory.Id
                    Api.authorityList.Id
                    Api.authorityGrants.Id
                ]

        {
            Operands =
                [
                    if caseScoped then
                        [ "caseReference" ]
                    if
                        List.contains
                            operation.Endpoint
                            [ Api.operationObserve.Id; Api.operationResolve.Id ]
                    then
                        [ "operationId" ]
                    if operation.Endpoint = Api.authorityGrants.Id then
                        [ "actorId" ]
                ]
            Prefill =
                [
                    if caseScoped then
                        [ "caseReference" ]
                    match operation.Act with
                    | Some(ActKind.Case(CaseActKind.Business(BusinessActKind.AmendRegistration | BusinessActKind.Decide))) ->
                        [ "act"; "values" ]
                    | Some(ActKind.Case(CaseActKind.Business BusinessActKind.Correct)) ->
                        for group in Vocabulary.all RecordGroup.vocabulary do
                            [ "act"; "groups"; RecordGroup.token group; "values" ]
                    | _ -> ()
                ]
            Fixed =
                match operation.Act with
                | Some(ActKind.Case(CaseActKind.Business BusinessActKind.Open)) ->
                    [ [ "expectedRevision" ], BindingValue.Integer 0L ]
                | _ -> []
            Presence =
                match operation.Act with
                | Some(ActKind.Authority(AuthorityKind.Grant | AuthorityKind.Revoke)) ->
                    [ [ "act"; "scope"; "kind" ] ]
                | _ -> []
            Switches =
                if operation.Endpoint = Api.caseHistory.Id then
                    [
                        [ "detail" ],
                        (Vocabulary.token HistoryDetails.vocabulary HistoryDetail.Summary,
                         Vocabulary.token HistoryDetails.vocabulary HistoryDetail.Full)
                    ]
                else
                    []
            Defaults =
                if paged then
                    [ [ "limit" ], BindingValue.Integer(int64 Limits.DefaultPageSize) ]
                else
                    []
            Minted =
                if operation.Endpoint = Api.operationExecute.Id then
                    [ [ "operationId" ] ]
                else
                    []
        }

    let private selected operation =
        match operation.Act with
        | Some act ->
            [
                ActKinds.token act
                match act with
                | ActKind.Case _ -> OperationSubjects.Case
                | ActKind.Authority _ -> OperationSubjects.Authority
            ]
        | None -> []

    let private schemaOf operation =
        (Api.tryFind operation.Endpoint
         |> Option.defaultWith (fun () -> invalidArg (nameof operation) "Unknown catalog endpoint."))
            .Accept
            { new IEndpointVisitor<Map<string, Schema> * Schema> with
                member _.Visit endpoint =
                    endpoint.Input.Definitions, endpoint.Input.Schema
            }

    let rec private resolve definitions =
        function
        | Schema.Ref name -> resolve definitions (Map.find name definitions)
        | schema -> schema

    let private exists operation definitions path root =
        let selected = selected operation

        let rec walk path schema =
            match resolve definitions schema with
            | _ when List.isEmpty path -> true
            | Schema.OneOf(cases, Some(_, mapping)) ->
                match mapping |> List.tryFind (fun (token, _) -> List.contains token selected) with
                | Some(_, name) -> walk path definitions[name]
                | None -> cases |> List.exists (walk path)
            | Schema.Object(properties, _) ->
                properties
                |> List.tryFind (fst >> (=) path.Head)
                |> Option.exists (snd >> walk path.Tail)
            | _ -> false

        walk path root

    let validateAnnotations operation notes =
        let definitions, root = schemaOf operation

        let paths =
            notes.Operands
            @ notes.Prefill
            @ notes.Presence
            @ notes.Minted
            @ (notes.Fixed |> List.map fst)
            @ (notes.Switches |> List.map fst)
            @ (notes.Defaults |> List.map fst)

        let missing =
            paths
            |> List.distinct
            |> List.filter (fun path -> not (exists operation definitions path root))

        if missing.IsEmpty then Ok() else Error missing

    let private nameFor path =
        match List.rev path with
        | "kind" :: group :: "groups" :: _ ->
            CatalogInputName.Group(
                Vocabulary.tryParse RecordGroup.vocabulary group
                |> Option.defaultWith (fun () -> invalidOp "Unknown record group in request codec.")
            )
        | "kind" :: "scope" :: _ -> CatalogInputName.Technical TechnicalInput.Scope
        | "detail" :: _ -> CatalogInputName.Technical TechnicalInput.Full
        | key :: _ ->
            match Vocabulary.tryParse FieldTable.vocabulary key with
            | Some field -> CatalogInputName.Field field
            | None ->
                CatalogInputName.Technical(
                    Vocabulary.tryParse TechnicalInputs.vocabulary key
                    |> Option.defaultWith (fun () ->
                        invalidOp (
                            "Unknown catalog input in request codec: " + String.concat "." path
                        ))
                )
        | [] -> invalidOp "A catalog input requires a member path."

    let private construct operation =
        let notes = annotations operation

        validateAnnotations operation notes
        |> Result.defaultWith (fun paths ->
            invalidOp (
                "Unknown catalog annotation path: "
                + (paths |> List.map (String.concat ".") |> String.concat ", ")
            ))

        let definitions, root = schemaOf operation
        let selected = selected operation

        let argument path =
            if List.contains path notes.Operands then
                CatalogArgument.Operand
            elif List.contains path notes.Presence then
                CatalogArgument.Implicit
            else
                CatalogArgument.Named

        let binding path branches required kind =
            let name = nameFor path
            let switch = notes.Switches |> List.tryFind (fst >> (=) path) |> Option.map snd

            let defaultValue =
                notes.Defaults |> List.tryFind (fst >> (=) path) |> Option.map snd

            let atom =
                match switch, kind with
                | Some(off, on), _ -> CatalogAtom.Switch(off, on)
                | _, BindingKind.Integer -> CatalogAtom.Integer
                | _, BindingKind.Boolean -> CatalogAtom.Boolean
                | _, (BindingKind.Group values | BindingKind.Choice values) ->
                    CatalogAtom.Choice values
                | _ -> CatalogAtom.Text

            let prefill =
                match name with
                | CatalogInputName.Field field when
                    notes.Prefill
                    |> List.exists (fun prefix -> List.truncate prefix.Length path = prefix)
                    ->
                    let row = FieldTable.forField field

                    Some(
                        match row.Placement with
                        | FieldPlacement.TopLevel -> [ row.Key ]
                        | FieldPlacement.Grouped group -> [ RecordGroup.token group; row.Key ]
                    )
                | _ -> None

            {
                Name = name
                Atom = atom
                Argument = argument path
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
                        Required =
                            required
                            && defaultValue.IsNone
                            && switch.IsNone
                            && not (List.contains path notes.Minted)
                            && not (List.contains path notes.Presence)
                        Default = defaultValue
                        DiagnosticTargets = diagnostics name
                        Source = source operation name
                    }
            }

        let constant path branches value =
            {
                Path = path
                Value = value
                Branches = branches
            }

        let combine values =
            values |> List.fold (fun (i, c) (i2, c2) -> i @ i2, c @ c2) ([], [])

        let rec walk path branches required schema =
            match resolve definitions schema with
            | Schema.Object(properties, optional) ->
                properties
                |> List.map (fun (name, child) ->
                    walk
                        (path @ [ name ])
                        branches
                        (required && not (List.contains name optional))
                        child)
                |> combine
            | Schema.OneOf(_, Some(tag, mapping)) ->
                let tagPath = path @ [ tag ]

                match mapping |> List.tryFind (fun (token, _) -> List.contains token selected) with
                | Some(token, name) ->
                    let inputs, constants = variant path branches required tag definitions[name]
                    inputs, constant tagPath branches (BindingValue.Text token) :: constants
                | None ->
                    let selector =
                        binding tagPath branches required (BindingKind.Group(List.map fst mapping))

                    let inputs, constants =
                        mapping
                        |> List.map (fun (token, name) ->
                            variant
                                path
                                (branches @ [ { Path = tagPath; Value = token } ])
                                required
                                tag
                                definitions[name])
                        |> combine

                    selector :: inputs, constants
            | leaf ->
                match notes.Fixed |> List.tryFind (fst >> (=) path), leaf with
                | Some(_, value), _ -> [], [ constant path branches value ]
                | None, Schema.Const value ->
                    [], [ constant path branches (BindingValue.Text value) ]
                | None, _ ->
                    let kind =
                        match leaf with
                        | Schema.Integer _ -> BindingKind.Integer
                        | Schema.Boolean -> BindingKind.Boolean
                        | Schema.String _ -> BindingKind.Text
                        | Schema.OneOf(values, None) ->
                            BindingKind.Choice(
                                values
                                |> List.map (function
                                    | Schema.Const value -> value
                                    | _ ->
                                        invalidOp "A catalog choice must contain string constants.")
                            )
                        | _ ->
                            invalidOp (
                                "Unsupported catalog input schema: " + String.concat "." path
                            )

                    [ binding path branches required kind ], []

        and variant path branches required tag schema =
            match resolve definitions schema with
            | Schema.Object(properties, optional) ->
                walk
                    path
                    branches
                    required
                    (Schema.Object(properties |> List.filter (fst >> (<>) tag), optional))
            | _ -> invalidOp "A tagged request branch must be an object."

        let inputs, constants = walk [] [] true root

        inputs
        |> List.mapi (fun order input ->
            { input with
                Binding = { input.Binding with Order = order }
            }),
        constants

    let private derived =
        all |> List.map (fun operation -> operation, construct operation) |> Map.ofList

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
                if entry.Endpoint = Api.operationExecute.Id then
                    Some(
                        Vocabulary.tryParse ActKinds.vocabulary entry.Kind
                        |> Option.defaultWith (fun () ->
                            invalidArg (nameof entry) "Unknown catalog act.")
                    )
                else
                    None
        }
