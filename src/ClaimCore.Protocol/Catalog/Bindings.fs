namespace ClaimCore.Protocol

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
            | Schema.Vocabulary(name, schema) ->
                visit { position with Vocabulary = Some name } schema
            | Schema.Ref name ->
                if Set.contains name position.References then
                    refuse ("recursive request definition " + name)

                visit
                    { position with
                        Definition = name
                        References = Set.add name position.References
                    }
                    (definition definitions name)
            | Schema.Object(properties, optional) -> objects position properties optional
            | Schema.OneOf(_, Some(tag, mapping)) -> tagged position tag mapping
            | Schema.OneOf(cases, None) ->
                input position (BindingKind.Choice(vocabulary position, enumTokens cases)) schema
                |> List.singleton
            | Schema.String _ -> [ input position BindingKind.Text schema ]
            | Schema.Integer _ -> [ input position BindingKind.Integer schema ]
            | Schema.Boolean -> [ input position BindingKind.Boolean schema ]
            | Schema.Const token -> [ constant position (BindingValue.Text token) ]
            | Schema.Array(items, _, _) ->
                let name, choices = finiteChoices definitions items
                [ input position (BindingKind.Choices(name, choices)) schema ]
            | Schema.Null -> refuse "request input is not a finite scalar or tagged object"

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
            }
            schema

    let keys nodes =
        nodes
        |> List.choose (function
            | BindingNode.Input value -> Some value.Member
            | _ -> None)
        |> Set.ofList

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
        | Schema.String(pattern, _, minimum, maximum), BindingValue.Text value ->
            (minimum |> Option.forall (fun bound -> value.Length >= bound))
            && (maximum |> Option.forall (fun bound -> value.Length <= bound))
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

    let private bindInput annotation input =
        annotation.Default |> Option.iter (validateValue input)

        {
            Name = annotation.Name
            Binding =
                {
                    Identity = String.concat "." input.Path
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
                    MeaningId = Some(InteractionInput.messageKey annotation.Name "meaning")
                    Order = 0
                    Prefill = annotation.Prefill
                    Required = authoredRequired input annotation
                    WireRequired = input.Required
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

    let bind annotations nodes =
        validateRegistry annotations (keys nodes)

        let inputs, constants =
            nodes
            |> List.fold
                (fun (inputs, constants) node ->
                    match node with
                    | BindingNode.Constant value -> inputs, constants @ [ value ]
                    | BindingNode.Input input ->
                        let annotation = Map.find input.Member annotations

                        match annotation.Fixed with
                        | Some value ->
                            validateValue input value

                            inputs,
                            constants
                            @ [
                                {
                                    Path = input.Path
                                    Value = value
                                    Branches = input.Branches
                                    Vocabulary = BindingKind.vocabulary input.Kind
                                }
                            ]
                        | None -> inputs @ [ bindInput annotation input ], constants)
                ([], [])

        inputs
        |> List.mapi (fun order input ->
            { input with
                Binding = { input.Binding with Order = order }
            }),
        constants
