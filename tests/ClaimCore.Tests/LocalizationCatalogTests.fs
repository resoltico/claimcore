module ClaimCore.Tests.LocalizationCatalogTests

open System.IO
open Expecto
open ClaimCore.Contract
open ClaimCore.TestSupport

let private directory () =
    Path.Combine(RepositoryRoot.find (), "src/ClaimCore.Contract/Localization")

let private families =
    [
        "field"
        "command"
        "group"
        "authority"
        "diagnostic"
        "fault"
        "host"
        "notice"
        "token"
        "ui"
        "cli"
    ]

let private entries language family =
    let bytes =
        File.ReadAllBytes(Path.Combine(directory (), language + "." + family + ".json"))

    match StrictJson.read bytes.Length bytes |> Fixtures.accepted with
    | JsonValue.Object entries ->
        entries
        |> List.map (fun (key, value) ->
            match value with
            | JsonValue.String text -> key, text
            | _ -> failtest "Authored catalog values must be ICU strings.")
    | _ -> failtest "Authored catalog root must be an object."

let private catalog language =
    families
    |> List.collect (entries language)
    |> Messages.catalog
    |> Fixtures.accepted
    |> Messages.templates

let private inventory () =
    let expected =
        [
            for language in LocalizationPolicy.languages do
                for family in families do
                    language + "." + family + ".json"
        ]
        |> Set.ofList

    let actual =
        Directory.GetFiles(directory (), "*.json")
        |> Seq.map Path.GetFileName
        |> Set.ofSeq

    Expect.equal actual expected "Every language contains exactly the adopted message families."

    for family in families do
        let english = entries "en" family |> List.map fst |> Set.ofList

        for language in LocalizationPolicy.languages do
            let localized = entries language family |> List.map fst |> Set.ofList
            Expect.equal localized english "Authored keys have exact language parity."

            Expect.isTrue
                (localized
                 |> Set.forall (fun key ->
                     key.StartsWith(family + ".", System.StringComparison.Ordinal)))
                "Every key belongs to the file's family."

let private interpolation () =
    let english = catalog "en"

    for language in LocalizationPolicy.languages do
        let localized = catalog language

        for KeyValue(key, source) in english do
            Expect.equal
                (localized |> Map.find key |> Template.arguments)
                (Template.arguments source)
                "Translations preserve the producer's exact argument names."

let private producers () =
    for language in LocalizationPolicy.languages do
        let templates = catalog language

        for definition in Refusals.definitions do
            let key = "diagnostic." + definition.Id
            let supplied = definition.Parameters |> List.map _.Name |> Set.ofList

            Expect.equal
                (templates |> Map.find key |> Template.arguments)
                supplied
                "Refusal limit producers and localized holes agree."

        for _, code in CoreFaults.all do
            Expect.isEmpty
                (templates |> Map.find ("fault." + code) |> Template.arguments)
                "Fault messages require no hidden server prose."

        for reason in Protocol.HostFailures.all do
            let code = Protocol.HostFailures.code reason

            Expect.isEmpty
                (templates |> Map.find ("host." + code) |> Template.arguments)
                "Every public host code has a parameter-free localized message."

let private cliArguments () =
    let limits =
        Set.ofList
            [
                "maximumInvocationBytes"
                "maximumRequestBytes"
                "maximumResponseBytes"
                "maximumSecretBytes"
                "maximumTrustRootBytes"
            ]

    for language in LocalizationPolicy.languages do
        let templates =
            entries language "cli"
            |> Messages.catalog
            |> Fixtures.accepted
            |> Messages.templates

        for KeyValue(key, template) in templates do
            let arguments = Template.arguments template

            match key with
            | "cli.configurationIssue" ->
                Expect.equal
                    arguments
                    (Set.ofList [ "setting"; "issue" ])
                    "Configuration producers supply only safe technical facts."
            | "cli.help.layout" ->
                Expect.equal
                    arguments
                    (Set.singleton "body")
                    "The shared help layout receives one rendered body."
            | _ ->
                Expect.isTrue
                    (Set.isSubset arguments limits)
                    "Help holes come only from authoritative byte limits."

[<Tests>]
let tests =
    testList
        "authored localization catalogs"
        [
            testCase "all language families and owned keys exist" inventory
            testCase "all catalog templates retain language parity" interpolation
            testCase "refusal fault and host producers match templates" producers
            testCase "CLI template arguments match their producers" cliArguments
        ]
