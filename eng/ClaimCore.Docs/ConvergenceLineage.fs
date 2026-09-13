namespace ClaimCore.Docs

open System
open System.Text.Json

[<RequireQualifiedAccess>]
module ConvergenceLineage =
    let private ordinal =
        List.sortWith (fun left right -> StringComparer.Ordinal.Compare(left, right))

    let private sha256 (value: string) =
        value.Length = 64
        && value
           |> Seq.forall (fun character ->
               (character >= '0' && character <= '9') || (character >= 'a' && character <= 'f'))

    let private substantiveReason status (reason: string) =
        if status = "replaced" then
            reason.Length >= 100
            && not (
                reason.Contains("legacy identity was removed", StringComparison.OrdinalIgnoreCase)
            )
            && not (
                reason.Contains(
                    "declared live typed replacement",
                    StringComparison.OrdinalIgnoreCase
                )
            )
        else
            reason.Length >= 24

    let private entry (element: JsonElement) =
        ConvergenceJson.exact [ "baselineId"; "reason"; "replacementIds"; "status" ] element
        |> Result.bind (fun () ->
            match
                ConvergenceJson.text "baselineId" element,
                ConvergenceJson.text "status" element,
                ConvergenceJson.text "reason" element,
                ConvergenceJson.strings "replacementIds" element
            with
            | Ok baselineId, Ok status, Ok reason, Ok replacements when
                (status = "retained" && replacements = [ baselineId ])
                || (status = "replaced" && not (replacements |> List.contains baselineId))
                ->
                if substantiveReason status reason then
                    Ok(baselineId, replacements)
                else
                    Error "Replaced lineage needs a subject-specific assertion reason."
            | Ok _, Ok _, Ok _, Ok _ ->
                Error "Lineage status and replacement identities are inconsistent."
            | Error message, _, _, _
            | _, Error message, _, _
            | _, _, Error message, _
            | _, _, _, Error message -> Error message)

    let private entries items =
        let parsed = items |> List.map entry

        let failures =
            parsed
            |> List.choose (function
                | Error message -> Some message
                | Ok _ -> None)

        match failures with
        | first :: _ -> Error first
        | [] ->
            let entries = parsed |> List.choose Result.toOption
            let identifiers = entries |> List.map fst

            if
                identifiers = ordinal identifiers
                && identifiers.Length = (identifiers |> Set.ofList |> Set.count)
            then
                Ok(Map.ofList entries)
            else
                Error "Lineage baseline IDs must be unique and ordinally sorted."

    let private validate (element: JsonElement) =
        ConvergenceJson.exact [ "baselineSha256"; "entries"; "schemaVersion" ] element
        |> Result.bind (fun () ->
            match
                ConvergenceJson.integer "schemaVersion" element,
                ConvergenceJson.text "baselineSha256" element,
                ConvergenceJson.array "entries" element
            with
            | Ok 1, Ok digest, Ok items when sha256 digest ->
                entries items
                |> Result.map (fun entries ->
                    {
                        BaselineSha256 = digest
                        Entries = entries
                    })
            | Ok _, Ok _, Ok _ -> Error "Lineage schema or baseline digest is invalid."
            | Error message, _, _
            | _, Error message, _
            | _, _, Error message -> Error message)

    let read root relative =
        ConvergenceJson.document root relative (fun _ element -> validate element)
