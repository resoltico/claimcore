namespace ClaimCore.Wire

open ClaimCore.Json
open ClaimCore.Presentation

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

            let joins =
                columns
                |> List.choose (fun column ->
                    match JsonValue.tryMember "key" column with
                    | Some(JsonValue.String key) ->
                        let visible =
                            JsonValue.tryMember "prominence" column
                            <> Some(JsonValue.String "DETAIL")

                        let target =
                            match JsonValue.tryMember "joins" column with
                            | Some(JsonValue.String target) -> Some target
                            | _ -> None

                        Some(key, visible, target)
                    | _ -> None)

            if
                unique (keys columns)
                && Column.validJoins joins
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
                    "Table columns and rows require valid joins, unique keys, matching widths and disjoint covering groups."

    let singletonRegions =
        ClaimCore.Presentation.Screen.singletonRoles
        |> List.map (ClaimCore.Domain.Vocabulary.token ClaimCore.Presentation.RegionRole.vocabulary)

    let regions path =
        function
        | JsonValue.Array values ->
            let roles =
                values
                |> List.choose (JsonValue.tryMember "role")
                |> List.filter (fun role ->
                    singletonRegions |> List.exists (JsonValue.String >> (=) role))

            if unique roles then
                Ok()
            else
                fail path "Only FACTS may repeat within a screen."
        | _ -> fail path "Expected ordered regions."

    let screen path value =
        let groups =
            array "currentGroups" value |> List.choose (JsonValue.tryMember "group")

        if unique groups then
            Ok()
        else
            fail path "Current groups must be unique."
