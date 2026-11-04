namespace ClaimCore.Protocol

open ClaimCore.Domain
open ClaimCore.Json

/// The declaration generates the closed wire row and its typed client schema, including optional group presence.
module ListedCaseFieldCodecs =
    open Codec

    let create (declarations: FieldDescriptor list) =
        let fields = declarations |> List.filter _.Listed
        let names = fields |> List.map _.Field |> Set.ofList

        for field in declarations do
            match field.Pairs with
            | Some paired when field.Listed <> Set.contains paired names ->
                invalidArg
                    (nameof declarations)
                    "Listed money fields require both monetary components."
            | _ -> ()

        let optionalGroups =
            fields
            |> List.choose (fun field ->
                field.Group
                |> Option.filter (fun group ->
                    group <> RecordGroup.Registration && field.AllowsAbsence))
            |> List.distinct

        let combinations =
            optionalGroups
            |> List.fold
                (fun combinations group ->
                    combinations @ (combinations |> List.map (Set.add group)))
                [ Set.empty ]
            |> List.filter (fun present ->
                not (
                    List.contains RecordGroup.Decision optionalGroups
                    && List.contains RecordGroup.Payment optionalGroups
                )
                || not (Set.contains RecordGroup.Payment present)
                || Set.contains RecordGroup.Decision present)

        let codec present =
            fields
            |> List.map (fun field ->
                let scalar = CaseFieldCodecs.forKind field.Kind

                let value =
                    match field.Group with
                    | Some group when List.contains group optionalGroups ->
                        if Set.contains group present then
                            scalar |> map Some Option.get
                        else
                            nullValue |> map (fun () -> None) (fun _ -> ())
                    | _ when field.AllowsAbsence -> nullable scalar
                    | _ -> scalar |> map Some Option.get

                field.Field, field.Key, value)
            |> fieldMap

        let selects present (values: Map<BusinessField, CaseFieldValue option>) =
            fields
            |> List.forall (fun field ->
                match field.Group with
                | Some group when List.contains group optionalGroups ->
                    Map.tryFind field.Field values
                    |> Option.exists (fun value -> value.IsSome = Set.contains group present)
                | _ -> true)

        combinations
        |> List.map (fun present ->
            branch
                id
                (fun values -> if selects present values then Some values else None)
                (codec present))
        |> choice "ListedCaseFields"
