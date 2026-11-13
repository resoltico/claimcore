module internal ClaimCore.AcceptanceTests.CliDisclosure

open ClaimCore.Json

open System
open System.IO
open System.Text
open System.Text.Json
open System.Text.Json.Nodes
open Expecto
open CliCalls

let private original (fixture: PublishedFixture.Fixture) =
    let source =
        JsonNode.Parse(File.ReadAllBytes(Path.Combine(fixture.Root, "examples/01-open.json")))
        |> nonNull

    let input = source["input"] |> nonNull
    let reference = "CLI-DISCLOSURE-" + id ()
    input["operationId"] <- JsonValue.Create(id ())
    (input["target"] |> nonNull)["caseReference"] <- JsonValue.Create reference

    ((input["act"] |> nonNull)["values"] |> nonNull)["incidentDescription"] <-
        JsonValue.Create "Earlier history\nOriginal exact paragraph"

    Encoding.UTF8.GetBytes(source.ToJsonString()), input, reference

let private amend (input: JsonNode) =
    let pending = input.DeepClone()
    pending["operationId"] <- JsonValue.Create(id ())
    pending["expectedRevision"] <- JsonValue.Create 1L
    let act = pending["act"] |> nonNull
    act["kind"] <- JsonValue.Create "AMEND_REGISTRATION"

    act["values"] <-
        JsonSerializer.SerializeToNode
            {|
                claimedAmount = "2.0000"
                claimedCurrency = "EUR"
            |}

    pending

let private staleReview (fixture: PublishedFixture.Fixture) input reference =
    let pending = amend input
    let bytes = frame "operation.preview" pending
    FixtureFiles.write fixture.Directory "stale-review.frame.json" bytes |> ignore

    Expect.isOk
        (ClaimCore.Cli.Frames.admit bytes)
        "Targeted preview frame is independently admitted before dispatch"

    run fixture fixture.Environment bytes 0 |> require "REVIEWED" |> ignore

    let close =
        {|
            kind = "CASE"
            operationId = id ()
            target =
                {|
                    kind = "REFERENCE"
                    caseReference = reference
                |}
            expectedRevision = 1L
            act =
                {|
                    kind = "AMEND_REGISTRATION"
                    values =
                        {|
                            claimedAmount = "9876.5432"
                            claimedCurrency = "EUR"
                            incidentDate = "2026-09-07"
                            incidentNotificationDate = "2026-09-08"
                            incidentDescription = "Distinctive history\nSecond exact line"
                        |}
                |}
        |}

    automation fixture "operation.execute" close 0 |> require "ACCEPTED" |> ignore
    let refused = automation fixture "operation.execute" pending 2 |> require "REJECTED"

    Expect.equal
        (refused.GetProperty("code").GetString())
        "REVISION_CONFLICT"
        "Preview cannot authorize a stale write"

    Expect.equal
        (refused.GetProperty("actualRevision").GetInt64())
        2L
        "Current revision is authoritative"

let private withheldReceipt (receipt: JsonElement) =
    let snapshot = receipt.GetProperty("subject").GetProperty("snapshot")

    Expect.equal
        (snapshot.GetProperty("kind").GetString())
        "WITHHELD"
        "Superseded business snapshot requires current full-history permission"

    Expect.equal
        (snapshot.EnumerateObject() |> Seq.map _.Name |> Seq.toList)
        [ "kind" ]
        "Withheld contains only its discriminator and no business payload"

let private withheld expected reply =
    require expected reply |> withheldReceipt

let private summaryHistory fixture reference =
    let history =
        automation
            fixture
            "case.timeline"
            {|
                target =
                    {|
                        kind = "REFERENCE"
                        caseReference = reference
                    |}
                filter = "FACTS"
                limit = 50
            |}
            0
        |> require "SUCCEEDED"

    Expect.equal
        (history.GetProperty("items").GetArrayLength())
        2
        "Stale execution and replays add no accepted entries"

    for entry in history.GetProperty("items").EnumerateArray() do
        let changes = entry.GetProperty("changes")

        Expect.equal
            (changes.GetProperty("kind").GetString())
            "WITHHELD"
            "Metadata access withholds historical values"

        Expect.equal (changes.EnumerateObject() |> Seq.length) 1 "No historical facts leak"

let private directoryDetails fixture reference =
    let environment = interactiveEnvironment fixture "synthetic-owner" "valid"
    let subject = "detail-person-" + id ()
    let target = principal fixture "HUMAN" subject

    let apply act =
        run
            fixture
            environment
            (frame
                "operation.execute"
                {|
                    kind = "PEOPLE"
                    operationId = id ()
                    target = target
                    act = act
                |})
            0
        |> require "ACCEPTED"
        |> ignore

    apply
        {|
            kind = "REGISTER"
            profile =
                {|
                    displayName = subject
                    contact = "historical-only@example.test"
                |}
        |}

    apply
        {|
            kind = "SET_PROFILE"
            profile =
                {|
                    displayName = subject
                    contact = "current-only@example.test"
                |}
        |}

    let accounts =
        verb fixture environment [ "people"; "list"; "--text"; subject ] 0
        |> require "SUCCEEDED"

    let actorId =
        accounts.GetProperty("items").EnumerateArray()
        |> Seq.exactlyOne
        |> fun item -> item.GetProperty("actor").GetProperty("actorId").GetString() |> nonNull

    let noGrants =
        invoke fixture environment [ "people"; "grants"; actorId; "--format"; "text" ] Array.empty 0
        |> fun result -> Encoding.UTF8.GetString result.StandardOutput

    Expect.stringContains
        noGrants
        "current-only@example.test"
        "An empty real grant page retains its authorized account details."

    apply
        {|
            kind = "GRANT"
            role = "CASE_READER"
            scope = {| kind = "INSTALLATION" |}
        |}

    apply
        {|
            kind = "GRANT"
            role = "CASE_HANDLER"
            scope =
                {|
                    kind = "CASE"
                    target =
                        {|
                            kind = "REFERENCE"
                            caseReference = reference
                        |}
                |}
        |}

    let directory =
        invoke fixture environment [ "people"; "grants"; actorId; "--format"; "text" ] Array.empty 0
        |> fun result -> Encoding.UTF8.GetString result.StandardOutput

    Expect.stringContains directory "Case reader" "The installed role is readable"
    Expect.stringContains directory "All cases" "Installation-wide scope remains distinguishable"
    Expect.stringContains directory "Case handler" "The scoped role is readable"
    Expect.stringContains directory "One case" "Case scope remains distinguishable"

    let grantData =
        verb fixture environment [ "people"; "grants"; actorId ] 0
        |> require "SUCCEEDED"

    let scoped =
        grantData.GetProperty("items").EnumerateArray()
        |> Seq.find (fun grant ->
            grant.GetProperty("scope").GetProperty("kind").GetString() = "CASE")

    Expect.equal
        (scoped.GetProperty("scope").GetProperty("caseReference").GetString())
        reference
        "The canonical grant keeps its original case reference"

    let history =
        invoke
            fixture
            environment
            [ "people"; "history"; "--limit"; "50"; "--format"; "text" ]
            Array.empty
            0
        |> fun result -> Encoding.UTF8.GetString result.StandardOutput

    Expect.stringContains
        history
        "historical-only@example.test"
        "Historical contact survives a later profile change."

    Expect.stringContains history subject "Historical registered principal subject remains visible."

let private qualify fixture =
    let source, input, reference = original fixture
    let accepted = run fixture fixture.Environment source 0 |> require "ACCEPTED"
    staleReview fixture input reference

    let textTimeline () =
        invoke
            fixture
            fixture.Environment
            [ "case"; "timeline"; reference; "--format"; "text" ]
            Array.empty
            0
        |> fun result -> Encoding.UTF8.GetString result.StandardOutput

    let full = textTimeline ()

    for expected in
        [
            "1,000.00 EUR"
            "9,876.5432 EUR"
            "1 Aug 2026"
            "Distinctive history\nSecond exact line"
            "Earlier history\nOriginal exact paragraph"
        ] do
        Expect.stringContains
            full
            expected
            "Real endpoint-to-CLI full history preserves exact earlier and later facts."

    Expect.isTrue
        (System.Text.RegularExpressions.Regex.IsMatch(full, "7 Sept? 2026"))
        "The exact new date survives both supported ICU English month abbreviations."

    let target = principal fixture "SERVICE" "service"
    setGrant fixture target "CASE_REGISTRAR" true
    setGrant fixture target "CASE_HANDLER" false

    let current =
        automation
            fixture
            "case.get"
            {|
                target =
                    {|
                        kind = "REFERENCE"
                        caseReference = reference
                    |}
            |}
            0
        |> require "SUCCEEDED"

    Expect.equal
        (current.GetProperty("revision").GetInt64())
        2L
        "Current snapshot remains disclosed"

    let history = current.GetProperty("advice").GetProperty("history").GetString()
    Expect.equal history "SUMMARY" "Revoked full history disappears from scoped advice"

    run fixture fixture.Environment source 0 |> withheld "ALREADY_ACCEPTED"
    automation fixture "operation.preview" input 0 |> withheld "ALREADY_ACCEPTED"
    let operationId = accepted.GetProperty("operationId").GetString() |> nonNull

    lookupReceipt fixture operationId |> withheldReceipt

    directoryDetails fixture reference
    summaryHistory fixture reference
    let summary = textTimeline ()

    for secret in
        [
            "9,876.5432"
            "Distinctive history"
            "Second exact line"
            "Earlier history"
            "Original exact paragraph"
        ] do
        Expect.isFalse
            (summary.Contains secret)
            "SUMMARY history does not leak exact differences through presentation."

    Expect.isFalse
        (System.Text.RegularExpressions.Regex.IsMatch(summary, "7 Sept? 2026"))
        "SUMMARY presentation cannot leak the dated historical value on either platform."


let published () =
    task {
        use fixture = new PublishedFixture.Fixture()
        do! fixture.Start()
        setup fixture
        qualify fixture
        fixture.Complete()
    }
