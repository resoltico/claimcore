module ClaimCore.Tests.CliDescriptionTests

open System
open System.IO
open System.Text
open System.Text.Json
open Expecto
open ClaimCore.Contract
open ClaimCore.Cli
open ClaimCore.Contract.Protocol

let private rendered language selection =
    Discovery.describeText language selection
    |> Option.get
    |> Encoding.UTF8.GetString

let private text name (item: JsonElement) =
    item.GetProperty(name: string).GetString() |> nonNull

let private expectedLabel language (item: JsonElement) =
    let identity = text "labelId" item
    let domain = identity.Split('.')[0]
    let root = ClaimCore.TestSupport.RepositoryRoot.find ()

    use document =
        JsonDocument.Parse(
            File.ReadAllBytes(
                Path.Combine(
                    root,
                    "src/ClaimCore.Contract/Localization",
                    language + "." + domain + ".json"
                )
            )
        )

    document.RootElement.GetProperty(identity).GetString() |> nonNull

let private elements name (value: JsonElement) =
    let array = value.GetProperty(name: string)
    [ for item in array.EnumerateArray() -> item ]

let private references (input: JsonElement) =
    let kind = text "kind" input

    if kind = "FIELDS" then
        elements "fields" input
    elif kind = "CORRECTION_GROUPS" then
        elements "groups" input |> List.collect (elements "replaceFields")
    else
        failtest "Unsupported descriptor input shape"

let private assertReference language definitions output (input: JsonElement) =
    let name = text "fieldName" input
    let field = definitions |> List.find (fun field -> text "name" field = name)

    Expect.stringContains
        output
        (name + " — " + expectedLabel language field)
        "Declared field reference and label survive"

    for property in [ "prefill"; "currentField" ] do
        match input.TryGetProperty property with
        | true, value ->
            Expect.stringContains
                output
                (property + ": " + (value.GetString() |> nonNull))
                "Input source is visible"
        | _ -> ()

let private knownInputs () =
    let opening = rendered "en" (CliDescription.Commands(Some "OPEN"))

    for name in
        [
            "incidentDate"
            "incidentNotificationDate"
            "incidentCountry"
            "claimantName"
            "insurerName"
            "claimedAmount"
            "claimedCurrency"
        ] do
        Expect.stringContains
            opening
            ("fieldName: " + name + " — ")
            "Independent OPEN inputs are meaningful"

    let correction = rendered "en" (CliDescription.Commands(Some "CORRECT_CASE"))

    for value in
        [
            "registration"
            "decision"
            "payment"
            "paymentDecisionDate"
            "payableAmount"
            "payableCurrency"
            "paymentDate"
            "KEEP"
            "REPLACE"
            "CLEAR"
        ] do
        Expect.stringContains
            correction
            value
            "Independent correction groups, choices and replacement fields"

let private commandInputs () =
    use document = JsonDocument.Parse(ContractDiscovery.descriptors ())
    let root = document.RootElement

    let fields =
        [ "fields"; "inputFields" ]
        |> List.collect (fun key -> root.GetProperty(key).EnumerateArray() |> Seq.toList)

    for language in LocalizationPolicy.languages do
        for command in root.GetProperty("commands").EnumerateArray() do
            let output = rendered language (CliDescription.Commands(Some(text "kind" command)))

            for input in references (command.GetProperty "inputs") do
                assertReference language fields output input

            match command.GetProperty("inputs").TryGetProperty "groups" with
            | true, groups ->
                for group in groups.EnumerateArray() do
                    Expect.stringContains
                        output
                        (expectedLabel language group)
                        "Localized group meaning"

                    for action in group.GetProperty("actions").EnumerateArray() do
                        Expect.stringContains
                            output
                            (action.GetString() |> nonNull)
                            "Every available choice survives"
            | _ -> ()

    knownInputs ()

let private authorityInputs () =
    use document = JsonDocument.Parse(ContractDiscovery.descriptors ())
    let root = document.RootElement

    for language in LocalizationPolicy.languages do
        for command in root.GetProperty("authorityCommands").EnumerateArray() do
            let output = rendered language (CliDescription.Authority(Some(text "kind" command)))

            for input in
                [ root.GetProperty "authorityTarget" ]
                @ (command.GetProperty("inputs").EnumerateArray() |> Seq.toList) do
                Expect.stringContains
                    output
                    (text "fieldName" input + " — " + expectedLabel language input)
                    "Authority inputs use declared IDs"

                Expect.stringContains output (text "primitive" input) "Primitive is visible"

let private endpointRoutes () =
    for language in LocalizationPolicy.languages do
        for endpoint in Api.all do
            let output = rendered language (CliDescription.Endpoints(Some(Api.id endpoint)))

            Expect.stringContains
                output
                ("mutates: " + (if Api.mutates endpoint then "true" else "false"))
                "Mutation nature is explicit"

            for direction in [ "request"; "response" ] do
                Expect.stringContains
                    output
                    ("ClaimCore.Cli schema " + Api.id endpoint + " " + direction)
                    "Exact selected schema route"

    Expect.stringContains
        (rendered "en" (CliDescription.Endpoints(Some "operation.execute")))
        "mutates: true"
        "Execution is a mutation"

    Expect.stringContains
        (rendered "en" (CliDescription.Endpoints(Some "context")))
        "mutates: false"
        "Context is a read"

[<Tests>]
let tests =
    testList
        "human descriptor projection"
        [
            testCase
                "all command input references and choices survive in every language"
                commandInputs
            testCase "authority common and act inputs survive in every language" authorityInputs
            testCase
                "endpoint descriptions expose exact schema routes and mutation nature"
                endpointRoutes
        ]
