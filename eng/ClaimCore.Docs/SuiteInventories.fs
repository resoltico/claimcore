namespace ClaimCore.Docs

open System.IO
open System.Text.Json

[<RequireQualifiedAccess>]
module SuiteInventories =
    let private inventoryName (suite: JsonElement) =
        let id = suite.GetProperty("id").GetString()
        let mutable assembly = Unchecked.defaultof<JsonElement>

        let name =
            if suite.TryGetProperty("assembly", &assembly) then
                assembly.GetString()
            else
                id

        id, $"tests/inventory/{name}.txt"

    let read (root: RepositoryRoot) =
        try
            let registry = Path.Combine(root.Path, "config", "test-suites.json")

            match Repository.ensureExistingSafe root registry with
            | Error message -> Error message
            | Ok safe ->
                use document = JsonDocument.Parse(File.ReadAllText(safe))

                if document.RootElement.GetProperty("schemaVersion").GetInt32() <> 2 then
                    Error "The suite registry must use schema 2."
                else
                    let inventories =
                        document.RootElement.GetProperty("suites").EnumerateArray()
                        |> Seq.map inventoryName
                        |> Seq.toList

                    let resolved =
                        inventories
                        |> List.map (fun (id, relative) ->
                            Repository.registeredPath root relative
                            |> Result.bind (Repository.ensureExistingSafe root)
                            |> Result.map (fun path -> id, File.ReadAllLines(path)))

                    match
                        resolved
                        |> List.tryPick (function
                            | Error error -> Some error
                            | Ok _ -> None)
                    with
                    | Some error -> Error error
                    | None ->
                        resolved
                        |> List.choose (function
                            | Ok value -> Some value
                            | Error _ -> None)
                        |> Ok
        with error ->
            Error $"Registered test inventories could not be read: {error.GetType().Name}."
