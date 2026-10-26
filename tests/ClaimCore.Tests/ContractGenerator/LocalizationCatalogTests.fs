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

let private sameMutationEveryLanguage key transform =
    AuthoredMessages.all ()
    |> Map.map (fun _ entries ->
        entries
        |> List.map (fun (name, value) -> name, if name = key then transform value else value))

let private producerMutations () =
    let producers =
        [
            "cli.usage.invalidInteger", "value"
            "cli.delivery.unresolved", "resolveCommand"
            "cli.help.call", "maximumRequestBytes"
            "cli.help.configuration", "maximumTrustRootBytes"
            "cli.help.layout", "body"
        ]

    for key, argument in producers do
        for change in
            [
                (fun value -> value + " {unexpected}")
                (fun value -> value.Replace("{" + argument + "}", ""))
                (fun value -> value.Replace("{" + argument + "}", "{renamed}"))
            ] do
            Expect.throws
                (fun () ->
                    sameMutationEveryLanguage key change
                    |> CatalogValidation.validateCatalogs
                    |> ignore)
                ("Generator must reject changed actual producer arguments even with translation parity: "
                 + key)

    for key in
        [
            "act.OPEN.label"
            "act.OPEN.meaning"
            "act.OPEN.confirmation"
            "input.kind.label"
            "group.registration.meaning"
        ] do
        Expect.throws
            (fun () ->
                sameMutationEveryLanguage key (fun value -> value + " {unexpected}")
                |> CatalogValidation.validateCatalogs
                |> ignore)
            ("Catalog metadata is rendered without arguments: " + key)

let private actualProducers () =
    let catalogs = CatalogValidation.validate ()

    for case in Microsoft.FSharp.Reflection.FSharpType.GetUnionCases typeof<CliMessage> do
        let values = case.GetFields() |> Array.map (fun _ -> box "producer-value")

        let message =
            Microsoft.FSharp.Reflection.FSharpValue.MakeUnion(case, values)
            |> nonNull
            |> unbox<CliMessage>
            |> CliMessages.produce

        for language in LocalizationPolicy.languages do
            let rendered = Template.render (catalogs[language][message.Key]) message.Arguments
            Expect.isNonEmpty rendered "Every actual closed CLI producer renders in every language"

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

    for row in
        rows
        |> Array.filter (fun row ->
            not (
                (row.GetProperty("key").GetString() |> nonNull)
                    .StartsWith("ui.", System.StringComparison.Ordinal)
            )) do
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
            testCase "identical all-language producer mutations stop generation" producerMutations
            testCase "actual CLI producers render with their own argument contracts" actualProducers
            testCase "CLI Template engine reproduces every owned frozen message" golden
        ]

[<Tests>]
let unreferencedChrome =
    testCase "renderer catalogs reject unreferenced keys with positive controls" (fun () ->
        let good = AuthoredMessages.all ()
        CatalogValidation.validateCatalogs good |> ignore

        for prefix in [ "ui."; "cli." ] do
            let bad =
                good
                |> Map.map (fun _ entries -> (prefix + "unreferencedControl", "Unused") :: entries)

            Expect.throws
                (fun () -> CatalogValidation.validateCatalogs bad |> ignore)
                "The unused-key negative control is detected")
