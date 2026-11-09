namespace ClaimCore.Wire

open ClaimCore.Json

/// Relational rendering structure, applied after the child codecs admit all member shapes.
module internal PresentationStructure =
    let private array name value =
        match JsonValue.tryMember name value with
        | Some(JsonValue.Array values) -> values
        | _ -> []

    let private keys values =
        values |> List.choose (JsonValue.tryMember "key")

    let private unique values =
        (List.distinct values).Length = values.Length

    let private fail path reason = Error { Path = path; Reason = reason }

    let arguments path value =
        match value with
        | JsonValue.Array values when keys values |> unique -> Ok()
        | _ -> fail path "Message argument keys must be unique."

    let content path value =
        if JsonValue.tryMember "kind" value <> Some(JsonValue.String "TABLE") then
            Ok()
        else
            let columns = array "columns" value
            let rows = array "rows" value
            let groups = array "groups" value
            let rowKeys = keys rows
            let grouped = groups |> List.collect (array "rowKeys")

            let counts =
                groups
                |> List.forall (fun group ->
                    match JsonValue.tryMember "count" group with
                    | Some(JsonValue.Integer count) ->
                        count >= int64 (array "rowKeys" group).Length
                    | _ -> false)

            if
                unique (keys columns)
                && unique rowKeys
                && unique grouped
                && counts
                && (rows |> List.forall (fun row -> (array "values" row).Length = columns.Length))
                && (groups.IsEmpty || Set.ofList rowKeys = Set.ofList grouped)
            then
                Ok()
            else
                fail
                    path
                    "Table columns and rows require unique keys, matching widths and disjoint covering groups."

    let screen path value =
        let roles =
            array "regions" value
            |> List.choose (JsonValue.tryMember "role")
            |> List.filter ((<>) (JsonValue.String "FACTS"))

        if unique roles then
            Ok()
        else
            fail path "Only FACTS may repeat within a screen."
