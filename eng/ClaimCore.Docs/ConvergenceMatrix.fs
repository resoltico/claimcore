namespace ClaimCore.Docs

open System
open System.Text.Json

[<RequireQualifiedAccess>]
module ConvergenceMatrix =
    let private permitted =
        set
            [
                "cancellation"
                "cli"
                "core"
                "documentation"
                "migration"
                "protocol"
                "recovery"
                "webGui"
                "webHost"
            ]

    let private ordinal =
        List.sortWith (fun left right -> StringComparer.Ordinal.Compare(left, right))

    let private sha256 (value: string) =
        value.Length = 64
        && value
           |> Seq.forall (fun character ->
               (character >= '0' && character <= '9') || (character >= 'a' && character <= 'f'))

    let private entry (element: JsonElement) =
        let endpoint =
            match element.TryGetProperty("id") with
            | true, value when value.ValueKind = JsonValueKind.String ->
                let id = value.GetString() |> Option.ofObj |> Option.defaultValue ""
                id.StartsWith("endpoint-cli:") || id.StartsWith("endpoint-web:")
            | _ -> false

        let properties =
            if endpoint then
                [ "dimensions"; "id"; "outcomeTags"; "subject"; "tests" ]
            else
                [ "dimensions"; "id"; "subject"; "tests" ]

        ConvergenceJson.exact properties element
        |> Result.bind (fun () ->
            match
                ConvergenceJson.text "id" element,
                ConvergenceJson.text "subject" element,
                ConvergenceJson.strings "dimensions" element,
                ConvergenceJson.strings "tests" element
            with
            | Ok id, Ok subject, Ok dimensions, Ok tests when subject.Length >= 32 ->
                if dimensions |> List.forall permitted.Contains then
                    (if endpoint then
                         ConvergenceJson.strings "outcomeTags" element |> Result.map Some
                     else
                         Ok None)
                    |> Result.map (fun tags -> id, tests, tags)
                else
                    Error "Matrix dimensions must use the registered assurance vocabulary."
            | Ok _, Ok _, Ok _, Ok _ ->
                Error "Matrix subjects must state a substantive assurance claim."
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
            let identifiers = entries |> List.map (fun (id, _, _) -> id)

            if
                identifiers = ordinal identifiers
                && identifiers.Length = (identifiers |> Set.ofList |> Set.count)
            then
                let outcomes =
                    entries
                    |> List.choose (fun (id, _, tags) ->
                        tags |> Option.map (fun value -> id, value))
                    |> Map.ofList

                let entryTests =
                    entries |> List.map (fun (id, values, _) -> id, values) |> Map.ofList

                let tests = entries |> List.collect (fun (_, values, _) -> values) |> Set.ofList
                Ok(Set.ofList identifiers, entryTests, outcomes, tests)
            else
                Error "Matrix entry IDs must be unique and ordinally sorted."

    let private validate (element: JsonElement) =
        ConvergenceJson.exact [ "baselineSha256"; "entries"; "purpose"; "schemaVersion" ] element
        |> Result.bind (fun () ->
            match
                ConvergenceJson.integer "schemaVersion" element,
                ConvergenceJson.text "purpose" element,
                ConvergenceJson.text "baselineSha256" element,
                ConvergenceJson.array "entries" element
            with
            | Ok 1, Ok "CC-CONVERGE-001 assurance mapping", Ok digest, Ok items when sha256 digest ->
                entries items
                |> Result.map (fun (entryIds, entryTests, outcomes, tests) ->
                    {
                        BaselineSha256 = digest
                        EntryIds = entryIds
                        EntryTests = entryTests
                        OutcomeTags = outcomes
                        Tests = tests
                    })
            | Ok _, Ok _, Ok _, Ok _ ->
                Error "Matrix schema, purpose, or baseline digest is invalid."
            | Error message, _, _, _
            | _, Error message, _, _
            | _, _, Error message, _
            | _, _, _, Error message -> Error message)

    let read root relative =
        ConvergenceJson.document root relative (fun _ element -> validate element)
