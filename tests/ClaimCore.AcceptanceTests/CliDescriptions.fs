module internal ClaimCore.AcceptanceTests.CliDescriptions

open System.Collections.Generic
open System.Text
open System.Text.Json
open Expecto
open ClaimCore.Contract

let private invoke (fixture: PublishedFixture.Fixture) arguments source =
    ProcessRunner.dotnet 8_000 fixture.Cli arguments (Dictionary<string, string>()) source

let private description fixture language arguments =
    let result =
        invoke
            fixture
            ("describe" :: arguments @ [ "--format"; "text"; "--language"; language ])
            None

    Expect.equal result.ExitCode 0 "Published selected human description"
    Expect.equal result.StandardError.Length 0 "No prose on stderr"
    Encoding.UTF8.GetString result.StandardOutput

let private endpointDescription fixture language =
    let endpoint = description fixture language [ "endpoints"; "case.execute" ]

    for item in
        [
            "mutates: true"
            "ClaimCore.Cli schema case.execute request"
            "ClaimCore.Cli schema case.execute response"
        ] do
        Expect.stringContains
            endpoint
            item
            "Endpoint meaning and exact lookup routes survive publication"

let private selectedInputs fixture =
    for language in LocalizationPolicy.languages do
        let opening = description fixture language [ "commands"; "OPEN" ]

        for field in
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
                ("fieldName: " + field + " — ")
                "All seven OPEN fields have meaningful labels"

        let correction = description fixture language [ "commands"; "CORRECT_CASE" ]

        for item in
            [
                "registration"
                "decision"
                "payment"
                "KEEP"
                "REPLACE"
                "CLEAR"
                "paymentDecisionDate"
                "payableAmount"
                "payableCurrency"
                "paymentDate"
                "currentField: paymentDate"
            ] do
            Expect.stringContains
                correction
                item
                "Correction choices and replacement fields survive publication"

        let authority = description fixture language [ "authority"; "SET_GRANT" ]

        for item in [ "target"; "role"; "scope"; "active" ] do
            Expect.stringContains
                authority
                ("fieldName: " + item + " — ")
                "Common and act authority input labels survive"

        endpointDescription fixture language

let private diagnosticRoute fixture =
    let result =
        invoke fixture [ "call" ] (Some(Encoding.UTF8.GetBytes "{invalid-frame"))

    Expect.equal result.ExitCode 2 "Malformed input preserves protocol refusal"
    Expect.equal result.StandardError.Length 0 "Exactly one machine frame, no prose"
    use frame = JsonDocument.Parse result.StandardOutput
    let root = frame.RootElement
    Expect.equal (root.GetProperty("code").GetString()) "INVALID_FRAME" "Observed protocol category"
    Expect.equal (root.GetProperty("attemptKnowledge").GetString()) "NOT_SENT" "No dispatch"
    let id = root.GetProperty("diagnostic").GetProperty("id").GetString() |> nonNull
    let discovery = invoke fixture [ "describe"; "diagnostics" ] None
    Expect.equal discovery.ExitCode 0 "Offline diagnostic discovery"
    use messages = JsonDocument.Parse discovery.StandardOutput

    Expect.isTrue
        (messages.RootElement.TryGetProperty("diagnostic." + id) |> fst)
        "The actual returned ID resolves"

    for language in LocalizationPolicy.languages do
        let help = invoke fixture [ "help"; id; "--language"; language ] None
        Expect.equal help.ExitCode 0 "Returned ID is an admitted core help route"
        let guidance = Encoding.UTF8.GetString help.StandardOutput

        for route in
            [
                "ClaimCore.Cli schema invocation"
                "ClaimCore.Cli schema response"
                "ClaimCore.Cli help call"
                "ClaimCore.Cli help session"
                "NDJSON"
                "EOF"
            ] do
            Expect.stringContains guidance route "Repairable framing and schema guidance"

        let descriptions = description fixture language [ "diagnostics" ]

        Expect.stringContains
            descriptions
            ("diagnostic." + id)
            "Localized discovery retains exact ID"

        Expect.stringContains
            descriptions
            (guidance.TrimEnd())
            "Discovery and help share generated core guidance"

let qualify fixture =
    selectedInputs fixture
    diagnosticRoute fixture
