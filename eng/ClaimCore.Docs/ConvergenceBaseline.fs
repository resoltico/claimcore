namespace ClaimCore.Docs

open System
open System.Text.Json

type private BaselineSource =
    {
        Id: string
        Kind: string
        Inventory: string
        Tests: string list
    }

[<RequireQualifiedAccess>]
module ConvergenceBaseline =
    let expectedSha256 =
        "30b14fab0c28bdb6c3a3a1d231e8d55ab89fd2a475fc8c4fd2a3d6776c6311c5"

    let private ordinal =
        List.sortWith (fun left right -> StringComparer.Ordinal.Compare(left, right))

    let private failures results =
        results
        |> List.choose (function
            | Error message -> Some message
            | Ok _ -> None)

    let private sha256 (value: string) =
        value.Length = 64
        && value
           |> Seq.forall (fun character ->
               (character >= '0' && character <= '9') || (character >= 'a' && character <= 'f'))

    let private source (element: JsonElement) =
        ConvergenceJson.exact [ "id"; "inventory"; "kind"; "sourceSha256"; "tests" ] element
        |> Result.bind (fun () ->
            match
                ConvergenceJson.text "id" element,
                ConvergenceJson.text "kind" element,
                ConvergenceJson.text "inventory" element,
                ConvergenceJson.text "sourceSha256" element,
                ConvergenceJson.strings "tests" element
            with
            | Ok id, Ok kind, Ok inventory, Ok digest, Ok tests when sha256 digest ->
                Ok
                    {
                        Id = id
                        Kind = kind
                        Inventory = inventory
                        Tests = tests
                    }
            | Ok _, Ok _, Ok _, Ok _, Ok _ ->
                Error "Baseline source digest must be lowercase SHA-256."
            | Error message, _, _, _, _
            | _, Error message, _, _, _
            | _, _, Error message, _, _
            | _, _, _, Error message, _
            | _, _, _, _, Error message -> Error message)

    /// Historical registrations stay immutable; explicitly registered new producers may be added.
    let internal registrationsPreserved historical current =
        Set.isSubset (Set.ofList historical) (Set.ofList current)

    let private sources (items: JsonElement list) =
        let parsed = items |> List.map source

        match failures parsed with
        | first :: _ -> Error first
        | [] ->
            let sources = parsed |> List.choose Result.toOption
            let identifiers = sources |> List.map _.Id

            if
                identifiers <> ordinal identifiers
                || identifiers.Length <> (identifiers |> Set.ofList |> Set.count)
            then
                Error "Baseline source IDs must be unique and ordinally sorted."
            else
                let expected =
                    ConvergenceInventory.sources ()
                    |> List.map (fun item -> item.Id, item.Kind, item.Inventory)

                let actual = sources |> List.map (fun item -> item.Id, item.Kind, item.Inventory)

                if registrationsPreserved actual expected then
                    Ok sources
                else
                    Error "Baseline source registrations differ from the live discovery inventory."

    let private counts (sources: BaselineSource list) (element: JsonElement) =
        ConvergenceJson.exact [ "browser"; "dotnetMtp"; "frontendUnit"; "total" ] element
        |> Result.bind (fun () ->
            match
                ConvergenceJson.integer "dotnetMtp" element,
                ConvergenceJson.integer "frontendUnit" element,
                ConvergenceJson.integer "browser" element,
                ConvergenceJson.integer "total" element
            with
            | Ok dotnet, Ok frontend, Ok browser, Ok total ->
                let count kind =
                    sources
                    |> List.filter (fun source -> source.Kind = kind)
                    |> List.sumBy _.Tests.Length

                if
                    dotnet = count "dotnet-mtp"
                    && frontend = count "vitest"
                    && browser = count "playwright"
                    && total = (sources |> List.sumBy _.Tests.Length)
                then
                    Ok total
                else
                    Error "Baseline counts do not match its exact identities."
            | Error message, _, _, _
            | _, Error message, _, _
            | _, _, Error message, _
            | _, _, _, Error message -> Error message)

    let private header (element: JsonElement) =
        ConvergenceJson.exact
            [ "counts"; "identitySeparator"; "productVersion"; "schemaVersion"; "sources" ]
            element
        |> Result.bind (fun () ->
            match
                ConvergenceJson.integer "schemaVersion" element,
                ConvergenceJson.text "productVersion" element,
                ConvergenceJson.text "identitySeparator" element,
                ConvergenceJson.array "sources" element
            with
            | Ok 1, Ok "0.1.0", Ok "::", Ok sources -> Ok sources
            | Ok _, Ok _, Ok _, Ok _ -> Error "Baseline identity or schema version is invalid."
            | Error message, _, _, _
            | _, Error message, _, _
            | _, _, Error message, _
            | _, _, _, Error message -> Error message)

    let private validate path element =
        header element
        |> Result.bind sources
        |> Result.bind (fun sources ->
            counts sources (element.GetProperty("counts"))
            |> Result.bind (fun total ->
                if Repository.sha256File path <> expectedSha256 then
                    Error "The immutable v0.1 baseline bytes differ from the registered digest."
                else
                    let identities =
                        sources
                        |> List.collect (fun source ->
                            source.Tests |> List.map (fun name -> source.Id + "::" + name))

                    if identities.Length <> (identities |> Set.ofList |> Set.count) then
                        Error "Baseline test identities must be unique."
                    else
                        Ok
                            {
                                Identities = Set.ofList identities
                                SourceCount = sources.Length
                                Total = total
                            }))

    let read root relative =
        ConvergenceJson.document root relative validate
