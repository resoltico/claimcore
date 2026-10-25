module ClaimCore.Tests.LocalizationCatalogTests

open System.IO
open Expecto
open ClaimCore.Contract
open ClaimCore.Cli
open ClaimCore.TestSupport

let private directory () =
    Path.Combine(RepositoryRoot.find (), "src/ClaimCore.Contract/Localization")

let private families =
    [
        "field"
        "act"
        "group"
        "authority"
        "input"
        "diagnostic"
        "fault"
        "host"
        "notice"
        "token"
        "ui"
        "cli"
    ]

let private allEntries language =
    let bytes = File.ReadAllBytes(Path.Combine(directory (), language + ".json"))

    match StrictJson.read bytes.Length bytes |> Fixtures.accepted with
    | JsonValue.Object entries ->
        entries
        |> List.map (fun (key, value) ->
            match value with
            | JsonValue.String text -> key, text
            | _ -> failtest "Authored catalog values must be ICU strings.")
    | _ -> failtest "Authored catalog root must be an object."

let private entries language family =
    allEntries language
    |> List.filter (fun (key, _) -> key.StartsWith(family + ".", System.StringComparison.Ordinal))

let private catalog language =
    allEntries language
    |> Messages.catalog
    |> Fixtures.accepted
    |> Messages.templates

let private inventory () =
    let expected =
        LocalizationPolicy.languages
        |> List.map (fun language -> language + ".json")
        |> Set.ofList

    let actual =
        Directory.GetFiles(directory (), "*.json")
        |> Seq.map Path.GetFileName
        |> Set.ofSeq

    Expect.equal actual expected "Exactly one catalog per supported language"
    let english = allEntries "en" |> List.map fst |> Set.ofList

    for language in LocalizationPolicy.languages do
        let localized = allEntries language |> List.map fst |> Set.ofList
        Expect.equal localized english "Authored keys have exact language parity"

        Expect.isTrue
            (localized
             |> Set.forall (fun key ->
                 families
                 |> List.exists (fun family ->
                     key.StartsWith(family + ".", System.StringComparison.Ordinal))))
            "Prefixes own the message family"

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
            | "cli.help.verbs" ->
                Expect.equal
                    arguments
                    (Set.singleton "grammar")
                    "Verb help receives exactly the grammar derived from its producers."
            | _ ->
                Expect.isTrue
                    (Set.isSubset arguments limits)
                    "Help holes come only from authoritative byte limits."

let private embeddedCatalogs () =
    for language in LocalizationPolicy.languages do
        for key, source in allEntries language do
            let template = Template.parse source |> Fixtures.accepted

            let expected =
                Template.parts template
                |> List.map (function
                    | TemplatePart.Literal text -> text
                    | TemplatePart.Hole name -> "{" + name + "}")
                |> String.concat ""

            Expect.equal
                (ContractDiscovery.localized language key)
                expected
                "The shipped CLI embeds every authored message, including English."

        for topic in CliCommands.topics do
            let text = ContractDiscovery.helpTopic language topic
            Expect.isGreaterThan text.Length 100 "Every admitted help topic renders offline."
            Expect.isFalse (text.Contains "{maximum") "Help interpolates its authoritative bounds."

    let delivery = ContractDiscovery.helpTopic "en" "delivery"
    Expect.stringContains delivery "prepared request bytes" "Help describes the frozen body."
    Expect.stringContains delivery "claimcore call" "Exact resending remains available."
    Expect.stringContains delivery "claimcore operation resolve" "Final resolution is explicit."

    Expect.isFalse
        (delivery.Contains "original request bytes")
        "Lexical input is not the frozen body."

let private diagnosticDiscovery () =
    use document =
        System.Text.Json.JsonDocument.Parse(ContractDiscovery.presentation ())

    let actual = document.RootElement.EnumerateObject() |> Seq.map _.Name |> Set.ofSeq

    let expected =
        [
            yield!
                Refusals.definitions
                |> List.map (fun definition -> "diagnostic." + definition.Id)
            yield! CoreFaults.all |> List.map (fun (_, code) -> "fault." + code)
            yield!
                Protocol.HostFailures.all
                |> List.map (fun reason -> "host." + Protocol.HostFailures.code reason)
        ]
        |> Set.ofList

    Expect.equal
        actual
        expected
        "Discovery contains the entire current public diagnostic vocabulary."

    for language in LocalizationPolicy.languages do
        let bytes = Discovery.describeText language CliDescription.Diagnostics |> Option.get

        let text = System.Text.Encoding.UTF8.GetString bytes

        for key in
            [
                "diagnostic.ACCESS_RESOURCE_UNAVAILABLE"
                "fault.CORE_STORE_UNAVAILABLE"
                "host.CONTRACT_MISMATCH"
            ] do
            Expect.stringContains
                text
                (key + ": ")
                "Offline discovery renders each public message family."

        Expect.isFalse
            (text.Contains "diagnostic.CLI_")
            "Help topic aliases are not public diagnostic identities."

[<Tests>]
let tests =
    testList
        "authored localization catalogs"
        [
            testCase "all language families and owned keys exist" inventory
            testCase "all catalog templates retain language parity" interpolation
            testCase "refusal fault and host producers match templates" producers
            testCase "CLI template arguments match their producers" cliArguments
            testCase "CLI embeds and renders the authored catalogs" embeddedCatalogs
            testCase
                "diagnostic text discovery renders every supported language"
                diagnosticDiscovery
        ]
