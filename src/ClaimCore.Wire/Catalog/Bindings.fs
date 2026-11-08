namespace ClaimCore.Wire

open ClaimCore.Presentation

open System
open System.Text.RegularExpressions
open ClaimCore.Domain
open ClaimCore.Application.Contracts
open ClaimCore.Json

type BindingMember = string * string

type SchemaInput =
    {
        Member: BindingMember
        Path: string list
        Kind: BindingKind
        Schema: Schema
        Required: bool
        Branches: BindingCondition list
    }

[<RequireQualifiedAccess>]
type BindingNode =
    | Input of SchemaInput
    | Constant of BindingAssignment
    | Collection of SchemaCollection

and SchemaCollection =
    {
        Member: BindingMember
        Path: string list
        Required: bool
        Branches: BindingCondition list
        MinimumItems: int
        MaximumItems: int
        Rows: BindingNode list
    }

/// Interaction policy accompanies a structural input; it cannot manufacture a wire path or kind.
type BindingAnnotation =
    {
        Name: InteractionInput
        Source: InputSource
        Prefill: string list option
        Default: BindingValue option
        Implicit: (BindingValue * string) option
        Fixed: BindingValue option
    }

type OperationInputDefinition =
    {
        Name: InteractionInput
        Binding: CatalogInput
    }

type OperationCollectionDefinition =
    {
        Name: InteractionInput
        Binding: CatalogCollection
    }

type OperationBindings =
    {
        Inputs: OperationInputDefinition list
        Collections: OperationCollectionDefinition list
        Constants: BindingAssignment list
    }

module Bindings =
    type private Position =
        {
            Definition: string
            Member: BindingMember option
            Path: string list
            Required: bool
            Branches: BindingCondition list
            References: Set<string>
            Vocabulary: string option
            CollectionDepth: int
        }

    let private refuse detail =
        invalidOp ("Catalog schema binding refused: " + detail)

    let private definition definitions name =
        Map.tryFind name definitions
        |> Option.defaultWith (fun () -> refuse ("missing definition " + name))

    let rec private resolved definitions =
        function
        | Schema.Ref name -> definition definitions name |> resolved definitions
        | Schema.Vocabulary(_, schema) -> resolved definitions schema
        | schema -> schema

    let branchDefinition definitions name token =
        match definition definitions name |> resolved definitions with
        | Schema.OneOf(_, Some(_, mapping)) ->
            mapping
            |> List.tryFind (fst >> ((=) token))
            |> Option.map snd
            |> Option.defaultWith (fun () -> refuse ("undeclared branch " + name + ":" + token))
        | _ -> refuse (name + " is not a tagged definition")

    let private enumTokens cases =
        cases
        |> List.map (function
            | Schema.Const token -> token
            | _ -> refuse "non-scalar untagged alternative")

    let private vocabulary (position: Position) =
        position.Vocabulary
        |> Option.defaultWith (fun () ->
            refuse ("enumeration lacks its codec vocabulary at " + String.concat "." position.Path))

    let private input position kind schema =
        let memberName =
            position.Member |> Option.defaultWith (fun () -> refuse "unnamed input")

        BindingNode.Input
            {
                Member = memberName
                Path = position.Path
                Kind = kind
                Schema = schema
                Required = position.Required
                Branches = position.Branches
            }

    let private constant position value =
        BindingNode.Constant
            {
                Path = position.Path
                Value = value
                Branches = position.Branches
                Vocabulary = position.Vocabulary
                RequiresParent = not position.Required
            }

    let private branch definitions tag (token, target) =
        match definition definitions target |> resolved definitions with
        | Schema.Object(properties, optional) ->
            match List.tryFind (fst >> ((=) tag)) properties with
            | Some(_, Schema.Const declared) when declared = token ->
                target, properties |> List.filter (fst >> ((<>) tag)), optional
            | _ -> refuse ("branch discriminator disagrees with its declared token: " + target)
        | _ -> refuse ("tagged branch is not an object: " + target)

    let rec private finiteChoices definitions =
        function
        | Schema.Ref name -> definition definitions name |> finiteChoices definitions
        | Schema.Vocabulary(name, Schema.OneOf(cases, None)) -> name, enumTokens cases
        | _ -> refuse "array input requires a finite declared vocabulary"

    let walk definitions selections schema =
        let rec visit (position: Position) schema =
            match schema with
            | Schema.EnumPartition _ -> refuse "set partition constraints are response-only"
            | Schema.Vocabulary(name, schema) ->
                visit { position with Vocabulary = Some name } schema
            | Schema.Ref name -> referenced position name
            | Schema.Object(properties, optional) -> objects position properties optional
            | Schema.OneOf(_, Some(tag, mapping)) -> tagged position tag mapping
            | Schema.OneOf(cases, None) -> untagged position cases schema
            | Schema.String _ -> [ input position BindingKind.Text schema ]
            | Schema.Integer _ -> [ input position BindingKind.Integer schema ]
            | Schema.Boolean -> [ input position BindingKind.Boolean schema ]
            | Schema.Const token -> [ constant position (BindingValue.Text token) ]
            | Schema.Array(items, minimum, maximum) -> arrays position items minimum maximum schema
            | Schema.Null -> refuse "request input is not a finite scalar or tagged object"

        and referenced position name =
            if Set.contains name position.References then
                refuse ("recursive request definition " + name)

            visit
                { position with
                    Definition = name
                    References = Set.add name position.References
                }
                (definition definitions name)

        and arrays position items minimum maximum schema =
            match resolved definitions items with
            | Schema.Object _ ->
                if position.CollectionDepth <> 0 then
                    refuse "nested record collections are not supported"

                let minimum, maximum =
                    match minimum, maximum with
                    | minimum, maximum when
                        minimum >= 1 && maximum >= minimum && maximum < Int32.MaxValue
                        ->
                        minimum, maximum
                    | _ -> refuse "record collections require nonempty finite bounds"

                let memberKey =
                    position.Member
                    |> Option.defaultWith (fun () ->
                        refuse "record collection requires an authored member")

                let rows =
                    visit
                        { position with
                            Path = []
                            Branches = []
                            Required = true
                            Member = None
                            References = Set.empty
                            Vocabulary = None
                            CollectionDepth = 1
                        }
                        items

                [
                    BindingNode.Collection
                        {
                            Member = memberKey
                            Path = position.Path
                            Required = position.Required
                            Branches = position.Branches
                            MinimumItems = minimum
                            MaximumItems = maximum
                            Rows = rows
                        }
                ]
            | _ ->
                let name, choices = finiteChoices definitions items
                [ input position (BindingKind.Choices(name, choices)) schema ]

        and untagged position cases schema =
            match cases with
            | [ Schema.String _; Schema.Null ]
            | [ Schema.Null; Schema.String _ ] -> [ input position BindingKind.Text schema ]
            | _ ->
                [
                    input
                        position
                        (BindingKind.Choice(vocabulary position, enumTokens cases))
                        schema
                ]

        and objects position properties optional =
            properties
            |> List.collect (fun (name, schema) ->
                visit
                    { position with
                        Member = Some(position.Definition, name)
                        Vocabulary = None
                        Path = position.Path @ [ name ]
                        Required = position.Required && not (List.contains name optional)
                    }
                    schema)

        and tagged position tag mapping =
            let name = vocabulary position

            let alternatives =
                mapping |> List.map (fun entry -> entry, branch definitions tag entry)

            match Map.tryFind position.Definition selections with
            | Some token ->
                let _, (name, properties, optional) =
                    alternatives
                    |> List.tryFind (fun ((declared, _), _) -> declared = token)
                    |> Option.defaultWith (fun () ->
                        refuse ("undeclared branch " + position.Definition + ":" + token))

                let tagPosition =
                    { position with
                        Path = position.Path @ [ tag ]
                    }

                constant tagPosition (BindingValue.Text token)
                :: objects { position with Definition = name } properties optional
            | None ->
                let tokens = mapping |> List.map fst

                let selector =
                    input
                        { position with
                            Member = Some(position.Definition, tag)
                            Path = position.Path @ [ tag ]
                        }
                        (BindingKind.Group(name, tokens))
                        (Schema.OneOf(tokens |> List.map Schema.Const, None))

                let children =
                    alternatives
                    |> List.collect (fun ((token, _), (name, properties, optional)) ->
                        let condition =
                            {
                                Path = position.Path @ [ tag ]
                                Value = token
                            }

                        objects
                            { position with
                                Definition = name
                                Required = true
                                Branches = position.Branches @ [ condition ]
                            }
                            properties
                            optional)

                selector :: children

        visit
            {
                Definition = ""
                Member = None
                Path = []
                Required = true
                Branches = []
                References = Set.empty
                Vocabulary = None
                CollectionDepth = 0
            }
            schema

    let rec keys nodes =
        nodes
        |> List.collect (function
            | BindingNode.Input value -> [ value.Member ]
            | BindingNode.Collection value -> value.Member :: (keys value.Rows |> Set.toList)
            | BindingNode.Constant _ -> [])
        |> Set.ofList

    let rec inputWidth nodes =
        nodes
        |> List.sumBy (function
            | BindingNode.Input _ -> 1
            | BindingNode.Collection value -> 1 + value.MaximumItems * inputWidth value.Rows
            | BindingNode.Constant _ -> 0)

    let validateRegistry annotations actual =
        let declared = annotations |> Map.keys |> Set.ofSeq
        let missing = Set.difference actual declared
        let orphan = Set.difference declared actual

        if not missing.IsEmpty then
            refuse (sprintf "unannotated authored inputs %A" missing)

        if not orphan.IsEmpty then
            refuse (sprintf "orphan annotations %A" orphan)

    let private accepts schema value =
        match schema, value with
        | Schema.Integer(minimum, maximum), BindingValue.Integer value ->
            value >= minimum && value <= maximum
        | Schema.Boolean, BindingValue.Boolean _ -> true
        | Schema.String(pattern, _, minimum, maximum, maximumUtf8Bytes), BindingValue.Text value ->
            (minimum |> Option.forall (fun bound -> value.Length >= bound))
            && (maximum |> Option.forall (fun bound -> value.Length <= bound))
            && (maximumUtf8Bytes
                |> Option.forall (fun bound ->
                    System.Text.Encoding.UTF8.GetByteCount value <= bound))
            && (pattern
                |> Option.forall (fun pattern ->
                    Regex.IsMatch(value, pattern, RegexOptions.CultureInvariant)))
        | Schema.OneOf(cases, None), BindingValue.Text value ->
            List.contains (Schema.Const value) cases
        | Schema.Const expected, BindingValue.Text value -> value = expected
        | _ -> false

    let private validateValue input value =
        if not (accepts input.Schema value) then
            refuse (sprintf "value is outside schema at %A" input.Path)

    let private target =
        function
        | InteractionInput.Target(InputTarget.Field field) -> Some(InputTarget.Field field)
        | InteractionInput.Target InputTarget.Reason -> Some InputTarget.Reason
        | InteractionInput.Target InputTarget.Role -> Some InputTarget.Role
        | InteractionInput.Target InputTarget.Text -> Some InputTarget.Text
        | InteractionInput.Target InputTarget.DirectorySearch -> Some InputTarget.DirectorySearch
        | InteractionInput.Target(InputTarget.PaymentId as target)
        | InteractionInput.Target(InputTarget.ReminderId as target)
        | InteractionInput.Target(InputTarget.ReminderAction as target)
        | InteractionInput.Target(InputTarget.ReminderDueDate as target)
        | InteractionInput.Target(InputTarget.ExpectedWorkRevision as target)
        | InteractionInput.Target(InputTarget.ExpectedReminderRevision as target) -> Some target
        | _ -> None

    let private authoredRequired input annotation =
        match annotation.Implicit with
        | Some(value, reason) ->
            if String.IsNullOrWhiteSpace reason then
                refuse "implicit interaction needs a reason"

            validateValue input value
            false
        | None ->
            input.Required
            && annotation.Source <> InputSource.OperationIdentity
            && annotation.Default.IsNone

    let private bindInput annotation input : OperationInputDefinition =
        annotation.Default |> Option.iter (validateValue input)

        {
            Name = annotation.Name
            Binding =
                {
                    CatalogInput.Identity = String.concat "." input.Path
                    Field = target annotation.Name
                    Path = input.Path
                    Kind = input.Kind
                    AdviceGroup =
                        match input.Kind, annotation.Name with
                        | BindingKind.Group _, InteractionInput.Target(InputTarget.Group group) ->
                            Some group
                        | _ -> None
                    Branches = input.Branches
                    LabelId = InteractionInput.messageKey annotation.Name "label"
                    HintId = InteractionInput.hintId annotation.Name
                    RuleId = Some(InteractionInput.messageKey annotation.Name "rule")
                    Order = 0
                    Prefill = annotation.Prefill
                    Required = authoredRequired input annotation
                    WireRequired = input.Required
                    AllowsNull =
                        match input.Schema with
                        | Schema.OneOf(cases, None) -> List.contains Schema.Null cases
                        | _ -> false
                    Default =
                        annotation.Default
                        |> Option.orElseWith (fun () -> annotation.Implicit |> Option.map fst)
                    DiagnosticTargets =
                        match annotation.Name with
                        | InteractionInput.WorkChoice field -> [ BusinessField.key field ]
                        | InteractionInput.Target value -> [ InputTarget.token value ]
                        | _ -> []
                    Source = annotation.Source
                }
        }

    let rec bind annotations nodes =
        validateRegistry annotations (keys nodes)

        let folder
            (
                state:
                    OperationInputDefinition list *
                    OperationCollectionDefinition list *
                    BindingAssignment list
            )
            node
            =
            let inputs, collections, constants = state

            match node with
            | BindingNode.Constant value -> inputs, collections, constants @ [ value ]
            | BindingNode.Collection row ->
                let annotation = Map.find row.Member annotations

                if
                    annotation.Source <> InputSource.Authored
                    || annotation.Default.IsSome
                    || annotation.Fixed.IsSome
                    || annotation.Implicit.IsSome
                    || annotation.Prefill.IsSome
                then
                    refuse "record collections require directly authored rows"

                let field =
                    match annotation.Name with
                    | InteractionInput.Target field -> field
                    | _ -> refuse "record collection requires a semantic input target"

                let rowKeys = keys row.Rows

                let child =
                    bind
                        (annotations |> Map.filter (fun key _ -> Set.contains key rowKeys))
                        row.Rows

                if not child.Collections.IsEmpty then
                    refuse "nested record collections are not supported"

                let collection: OperationCollectionDefinition =
                    {
                        Name = annotation.Name
                        Binding =
                            {
                                CatalogCollection.Identity = String.concat "." row.Path
                                Field = field
                                Path = row.Path
                                Branches = row.Branches
                                LabelId = InteractionInput.messageKey annotation.Name "label"
                                HintId = InteractionInput.hintId annotation.Name
                                RuleId = Some(InteractionInput.messageKey annotation.Name "rule")
                                Order = collections.Length
                                Required = row.Required
                                MinimumItems = row.MinimumItems
                                MaximumItems = row.MaximumItems
                                RowInputs = child.Inputs |> List.map _.Binding
                                RowConstants = child.Constants
                            }
                    }

                inputs, collections @ [ collection ], constants
            | BindingNode.Input input ->
                let annotation = Map.find input.Member annotations

                match annotation.Fixed with
                | Some value ->
                    validateValue input value

                    inputs,
                    collections,
                    constants
                    @ [
                        {
                            Path = input.Path
                            Value = value
                            Branches = input.Branches
                            Vocabulary = BindingKind.vocabulary input.Kind
                            RequiresParent = false
                        }
                    ]
                | None -> inputs @ [ bindInput annotation input ], collections, constants

        let inputs, collections, constants = List.fold folder ([], [], []) nodes

        {
            OperationBindings.Inputs =
                inputs
                |> List.mapi (fun order (input: OperationInputDefinition) ->
                    { input with
                        Binding = { input.Binding with Order = order }
                    })
            Collections = collections
            Constants = constants
        }
