namespace ClaimCore.RecordFormat

open ClaimCore.Domain
open ClaimCore.Json

/// Canonical stored input maps retain semantic keys and explicit collection ordinals.
module InputMapCodecs =
    open Codec

    let atom =
        choice
            "InputAtom"
            [
                branch
                    InputValue.Text
                    (function
                    | InputValue.Text value -> Some value
                    | _ -> None)
                    text
                branch
                    InputValue.Integer
                    (function
                    | InputValue.Integer value -> Some value
                    | _ -> None)
                    (integer -SafeInteger.Maximum SafeInteger.Maximum)
                branch
                    InputValue.Boolean
                    (function
                    | InputValue.Boolean value -> Some value
                    | _ -> None)
                    boolean
                branch
                    (fun () -> InputValue.Null)
                    (function
                    | InputValue.Null -> Some()
                    | _ -> None)
                    nullValue
            ]

    let private values =
        TaskInputId.all
        |> List.map (fun input -> input, TaskInputId.token input, true, atom)
        |> fieldMapWithPresence

    let private row collection =
        (record (fun ordinal values -> ordinal, values)
        : Fields<int * Map<TaskInputId, InputValue>, _>)
        |> field
            "ordinal"
            fst
            (integer 0L (int64 (TaskCollectionId.maximumRows collection - 1))
             |> map int int64)
        |> field "values" snd values
        |> build

    let private collections =
        [ TaskCollectionId.PeopleGrants; TaskCollectionId.Parties ]
        |> List.map (fun collection ->
            let rows =
                boundedList 0 (TaskCollectionId.maximumRows collection) (row collection)
                |> interpret
                    (fun path rows ->
                        if
                            rows
                            |> List.mapi (fun ordinal (index, _) -> ordinal = index)
                            |> List.forall id
                        then
                            Ok(List.map snd rows)
                        else
                            Error
                                {
                                    Path = path
                                    Reason = "Collection ordinals must be contiguous."
                                })
                    (List.mapi (fun ordinal value -> ordinal, value))

            collection, TaskCollectionId.key collection, true, rows)
        |> fieldMapWithPresence

    let stored =
        let fields =
            (None,
             "collections",
             false,
             collections
             |> map Choice2Of2 (function
                 | Choice2Of2 value -> value
                 | _ -> invalidOp "The collection member has its declared type."))
            :: (TaskInputId.all
                |> List.map (fun input ->
                    Some input,
                    TaskInputId.token input,
                    true,
                    atom
                    |> map Choice1Of2 (function
                        | Choice1Of2 value -> value
                        | _ -> invalidOp "An input member has its declared scalar type.")))

        fieldMapWithPresence fields
        |> interpret
            (fun path members ->
                let values =
                    members
                    |> Map.toList
                    |> List.choose (function
                        | Some input, Choice1Of2 value -> Some(input, value)
                        | _ -> None)
                    |> Map.ofList

                let collections =
                    match Map.find None members with
                    | Choice2Of2 value -> value
                    | _ -> Map.empty

                InputMap.create values collections
                |> Option.map Ok
                |> Option.defaultValue (
                    Error
                        {
                            Path = path
                            Reason = "Invalid input collection."
                        }
                ))
            (fun (inputs: InputMap) ->
                inputs.Values
                |> Map.toList
                |> List.map (fun (input, value) -> Some input, Choice1Of2 value)
                |> Map.ofList
                |> Map.add
                    None
                    (Choice2Of2(
                        inputs.Collections |> Map.map (fun _ rows -> rows |> List.map _.Values)
                    )))
        |> named "StoredInputMap"

    let private keep retention (declaration: TaskDeclaration) =
        declaration.Inputs
        |> List.filter (fun input ->
            input.Source = InputSource.Authored && input.Retention = retention)
        |> List.map _.Input
        |> Set.ofList

    let partition retention (declaration: TaskDeclaration) (inputs: InputMap) =
        let selected = keep retention declaration

        let values =
            inputs.Values |> Map.filter (fun input _ -> Set.contains input selected)

        let collections =
            inputs.Collections
            |> Map.toList
            |> List.choose (fun (id, rows) ->
                let definition =
                    declaration.Collections
                    |> List.tryFind (fun collection -> collection.Collection = id)

                definition
                |> Option.bind (fun definition ->
                    let selected =
                        definition.Inputs
                        |> List.filter (fun input -> input.Retention = retention)
                        |> List.map _.Input
                        |> Set.ofList

                    // Retain the row positions for replay even when all row values are erasable.
                    if selected.IsEmpty && retention = InputRetention.Erasable then
                        None
                    else
                        Some(
                            id,
                            rows
                            |> List.map (fun row ->
                                row.Values
                                |> Map.filter (fun input _ -> Set.contains input selected))
                        )))
            |> Map.ofList

        InputMap.create values collections |> Option.get

    let hasErasable (declaration: TaskDeclaration) =
        declaration.Inputs
        |> List.exists (fun input ->
            input.Source = InputSource.Authored && input.Retention = InputRetention.Erasable)
        || (declaration.Collections
            |> List.exists (fun collection ->
                collection.Inputs
                |> List.exists (fun input -> input.Retention = InputRetention.Erasable)))

    let merge (left: InputMap) (right: InputMap) =
        let disjoint first second =
            Set.intersect (first |> Map.keys |> Set.ofSeq) (second |> Map.keys |> Set.ofSeq)
            |> Set.isEmpty

        if not (disjoint left.Values right.Values) then
            None
        else
            let collections =
                Set.union
                    (left.Collections |> Map.keys |> Set.ofSeq)
                    (right.Collections |> Map.keys |> Set.ofSeq)
                |> Set.toList

            let rows id =
                match Map.tryFind id left.Collections, Map.tryFind id right.Collections with
                | Some first, Some second when first.Length = second.Length ->
                    List.zip first second
                    |> List.map (fun (first, second) ->
                        if disjoint first.Values second.Values then
                            Some(
                                Map.fold
                                    (fun result key value -> Map.add key value result)
                                    first.Values
                                    second.Values
                            )
                        else
                            None)
                | Some rows, None
                | None, Some rows -> rows |> List.map (fun row -> Some row.Values)
                | _ -> [ None ]

            let collected = collections |> List.map (fun id -> id, rows id)

            if collected |> List.exists (snd >> List.exists Option.isNone) then
                None
            else
                InputMap.create
                    (Map.fold
                        (fun result key value -> Map.add key value result)
                        left.Values
                        right.Values)
                    (collected
                     |> List.map (fun (collection, rows) -> collection, rows |> List.choose id)
                     |> Map.ofList)
