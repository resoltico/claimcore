module ClaimCore.Tests.LocalizationCatalogTests

open System.IO
open System.Text.Json
open Expecto
open ClaimCore.Contract
open ClaimCore.ContractGeneration
open ClaimCore.Cli
open ClaimCore.TestSupport

let private change language transform =
    AuthoredMessages.all () |> Map.change language (Option.map transform)

let private invalidCatalogs () =
    for language, transform in
        [
            "lv", List.filter (fst >> (<>) "ui.cases")
            "ar",
            List.map (fun (key, value) ->
                key, if key = "ui.cases" then value + " {extra}" else value)
            "en",
            List.map (fun (key, value) ->
                key,
                if key = "ui.cases" then
                    "{count, plural, one {case} other {cases}}"
                else
                    value)
        ] do
        Expect.throws
            (fun () -> change language transform |> CatalogValidation.validateCatalogs |> ignore)
            "Generation rejects broken language/argument/grammar contracts"

let private golden () =
    use document =
        JsonDocument.Parse(
            File.ReadAllBytes(
                Path.Combine(RepositoryRoot.find (), "tests/fixtures/rendered-messages.json")
            )
        )

    let rows = document.RootElement.EnumerateArray() |> Seq.toArray

    let expected =
        AuthoredMessages.all ()
        |> Map.toList
        |> List.collect (fun (language, entries) ->
            entries |> List.map (fun (key, _) -> language, key))
        |> Set.ofList

    let actual =
        rows
        |> Array.map (fun row ->
            (row.GetProperty("language").GetString() |> nonNull),
            (row.GetProperty("key").GetString() |> nonNull))

    Expect.equal
        (Set.ofArray actual)
        expected
        "Every authored message is frozen in every real language"

    Expect.equal actual.Length expected.Count "Every rendering occurs exactly once"

    for row in rows do
        let language = row.GetProperty("language").GetString() |> nonNull
        let key = row.GetProperty("key").GetString() |> nonNull

        let args =
            row.GetProperty("arguments").EnumerateObject()
            |> Seq.map (fun property -> property.Name, property.Value.GetString() |> nonNull)
            |> Map.ofSeq

        Expect.equal
            (MessageCatalogs.render language key args)
            (row.GetProperty("expected").GetString() |> nonNull)
            (language + ": " + key)

[<Tests>]
let tests =
    testList
        "localization publication"
        [
            testCase "all supported catalogs pass the single generator validator" (fun () ->
                CatalogValidation.validate () |> ignore)
            testCase
                "missing Latvian key, extra Arabic hole and English ICU plural each stop generation"
                invalidCatalogs
            testCase "CLI Template engine reproduces every frozen renamed message" golden
        ]
