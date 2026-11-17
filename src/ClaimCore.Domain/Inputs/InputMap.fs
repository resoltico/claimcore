namespace ClaimCore.Domain

/// Row identity is supplied by the collection's order and survives retention splitting.
type InputRow =
    private
        {
            TargetValue: TaskRowTarget
            ValuesValue: Map<TaskInputId, InputValue>
        }

    member this.Target = this.TargetValue
    member this.Ordinal = TaskRowTarget.ordinal this.TargetValue
    member this.Values = this.ValuesValue

/// Structural input atoms have no transport dependency and do not imply business admission.
type InputMap =
    private
        {
            ValuesValue: Map<TaskInputId, InputValue>
            CollectionsValue: Map<TaskCollectionId, InputRow list>
        }

    member this.Values = this.ValuesValue
    member this.Collections = this.CollectionsValue

module InputMap =
    let empty =
        {
            ValuesValue = Map.empty
            CollectionsValue = Map.empty
        }

    let create values (collections: Map<TaskCollectionId, Map<TaskInputId, InputValue> list>) =
        let rows collection values =
            values
            |> List.mapi (fun ordinal value ->
                TaskRowTarget.create collection ordinal
                |> Option.map (fun target ->
                    {
                        TargetValue = target
                        ValuesValue = value
                    }))
            |> List.fold
                (fun result row ->
                    match result, row with
                    | Some previous, Some row -> Some(row :: previous)
                    | _ -> None)
                (Some [])
            |> Option.map List.rev

        collections
        |> Map.fold
            (fun result collection values ->
                match result, rows collection values with
                | Some previous, Some rows -> Some(Map.add collection rows previous)
                | _ -> None)
            (Some Map.empty)
        |> Option.map (fun collections ->
            {
                ValuesValue = values
                CollectionsValue = collections
            })
