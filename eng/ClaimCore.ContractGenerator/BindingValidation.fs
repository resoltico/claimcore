namespace ClaimCore.ContractGeneration

open ClaimCore.Contract

/// Checks exact member/branch bindings against the codec declaration before artifacts are emitted.
module BindingValidation =
    let private definitions = Api.operationExecute.Input.Definitions

    let rec private dereference =
        function
        | Schema.Ref name -> Map.find name definitions |> dereference
        | schema -> schema

    let private key path = String.concat "." path

    let private selected (values: Map<string, BindingValue>) path =
        Map.tryFind (key path) values
        |> Option.bind (function
            | BindingValue.Text text -> Some text
            | _ -> None)

    let rec private at values parent path schema =
        match dereference schema, path with
        | schema, [] -> schema
        | Schema.Object(properties, _), name :: rest ->
            properties
            |> List.tryFind (fst >> (=) name)
            |> Option.map (fun (_, schema) -> at values (parent @ [ name ]) rest schema)
            |> Option.defaultWith (fun () -> invalidOp "An interaction member path is absent.")
        | Schema.OneOf(_, Some(tag, mapping)), [ name ] when name = tag ->
            Schema.OneOf(mapping |> List.map (fst >> Schema.Const), None)
        | Schema.OneOf(_, Some(tag, mapping)), _ ->
            let token =
                selected values (parent @ [ tag ])
                |> Option.defaultWith (fun () -> invalidOp "An interaction branch is unspecified.")

            let target =
                mapping
                |> List.tryFind (fst >> (=) token)
                |> Option.map snd
                |> Option.defaultWith (fun () -> invalidOp "An interaction branch is absent.")

            at values parent path (Schema.Ref target)
        | _ -> invalidOp "An interaction path does not reach a declared member."

    let rec private constant value schema =
        match value, dereference schema with
        | value, Schema.OneOf(variants, None) ->
            List.filter (constant value) variants |> List.length = 1
        | BindingValue.Text value, Schema.Const expected -> value = expected
        | BindingValue.Text _, Schema.String _ -> true
        | BindingValue.Boolean _, Schema.Boolean -> true
        | BindingValue.Integer value, Schema.Integer(minimum, maximum) ->
            value >= minimum && value <= maximum
        | _ -> false

    let private choices variants =
        variants
        |> List.map (function
            | Schema.Const token -> token
            | _ -> invalidOp "A choice requires declared tokens.")
        |> Set.ofList

    let private kind expected schema =
        match expected, dereference schema with
        | BindingKind.Text, Schema.String _
        | BindingKind.Integer, Schema.Integer _
        | BindingKind.Boolean, Schema.Boolean -> true
        | BindingKind.Choice options, Schema.OneOf(variants, _)
        | BindingKind.Group options, Schema.OneOf(variants, _) ->
            Set.ofList options = choices variants
        | _ -> false

    let private values (interaction: InteractionDefinition) (input: InteractionInput) =
        interaction.Constants
        |> List.map (fun item -> key item.Path, item.Value)
        |> Map.ofList
        |> fun initial ->
            input.Branches
            |> List.fold
                (fun state branch ->
                    Map.add (key branch.Path) (BindingValue.Text branch.Value) state)
                initial

    let rec private leaves path conditions schema =
        match dereference schema with
        | Schema.Object(properties, optional) ->
            properties
            |> List.filter (fst >> fun name -> not (List.contains name optional))
            |> List.collect (fun (name, shape) -> leaves (path @ [ name ]) conditions shape)
        | Schema.OneOf(_, Some(tag, mapping)) ->
            mapping
            |> List.collect (fun (token, target) ->
                leaves
                    path
                    ({
                        BindingCondition.Path = path @ [ tag ]
                        Value = token
                     }
                     :: conditions)
                    (Schema.Ref target))
        | schema -> [ path, conditions, schema ]

    let private compatible constants conditions =
        conditions
        |> List.forall (fun (condition: BindingCondition) ->
            match Map.tryFind (key condition.Path) constants with
            | Some(BindingValue.Text value) -> value = condition.Value
            | Some _ -> false
            | None -> true)

    let private covered interaction (path, conditions, schema) =
        let constants =
            interaction.Constants
            |> List.map (fun item -> key item.Path, item.Value)
            |> Map.ofList

        if compatible constants conditions then
            match Map.tryFind (key path) constants with
            | Some value when constant value schema -> ()
            | _ ->
                let expected =
                    conditions
                    |> List.filter (fun branch ->
                        not (Map.containsKey (key branch.Path) constants) && branch.Path <> path)
                    |> Set.ofList

                if
                    not (
                        interaction.Inputs
                        |> List.exists (fun input ->
                            input.Path = path && Set.ofList input.Branches = expected)
                    )
                then
                    invalidOp (
                        "Required interaction binding is absent: "
                        + interaction.Scope
                        + "/"
                        + interaction.Kind
                        + "/"
                        + key path
                    )

    let private assignmentMap (items: BindingAssignment list) =
        items |> List.map (fun item -> key item.Path, item.Value) |> Map.ofList

    let private validateConstants selected (items: BindingAssignment list) =
        for item in items do
            if
                not (
                    constant item.Value (at selected [] item.Path Api.operationExecute.Input.Schema)
                )
            then
                invalidOp "An interaction constant does not match its declared member."

    let validate (interactions: InteractionDefinition list) =
        let identifiers =
            interactions
            |> List.map (fun interaction -> interaction.Scope, interaction.Kind)

        if Set.count (Set.ofList identifiers) <> identifiers.Length then
            invalidOp "Duplicate interaction definition."

        let knownFields = FieldTable.rows |> List.map _.Field |> Set.ofList

        for interaction in interactions do
            let identities = interaction.Inputs |> List.map _.Identity

            if Set.count (Set.ofList identities) <> identities.Length then
                invalidOp "Duplicate interaction identity."

            let selected = assignmentMap interaction.Constants
            validateConstants selected interaction.Constants

            for input in interaction.Inputs do
                match input.Source with
                | InputSource.OperationIdentity when input.Path <> [ "operationId" ] ->
                    invalidOp "Operation identity binds its declared technical member."
                | InputSource.ExpectedRevision when input.Path <> [ "expectedRevision" ] ->
                    invalidOp "Expected revision binds its declared technical member."
                | _ -> ()

            for input in interaction.Inputs do
                if
                    not (
                        kind
                            input.Kind
                            (at
                                (values interaction input)
                                []
                                input.Path
                                Api.operationExecute.Input.Schema)
                    )
                then
                    invalidOp
                        "An interaction binding kind or choice disagrees with its codec member."

                if
                    input.Prefill
                    |> Option.exists (fun field -> not (Set.contains field knownFields))
                then
                    invalidOp "An interaction prefill field is absent."


            leaves [] [] Api.operationExecute.Input.Schema
            |> List.iter (covered interaction)
