module internal ClaimCore.AcceptanceTests.RejectedDrafts

open ClaimCore.Contract

open System.IO
open System.Text.Json.Nodes
open Expecto
open CliCalls

let private input (fixture: PublishedFixture.Fixture) (field: string) value =
    let authored =
        JsonNode.Parse(File.ReadAllBytes(Path.Combine(fixture.Root, "examples/01-open.json")))
        |> nonNull

    let input = authored["input"] |> nonNull
    input["operationId"] <- JsonValue.Create(id ())
    input["caseReference"] <- JsonValue.Create("INVALID-" + id ())
    let values = (input["act"] |> nonNull)["values"] |> nonNull
    values[field] <- JsonValue.Create<string> value
    input

let private businessRefusals fixture =
    for field, value, diagnostic in
        [
            "incidentDate", "2026-02-30", "INPUT_CALENDAR_DATE_REQUIRED"
            "claimedAmount", "١٢٫٥٠", "INPUT_DECIMAL_FORMAT"
            "claimedAmount", "00.10", "INPUT_DECIMAL_FORMAT"
            "claimedCurrency", "ĀBC", "INPUT_CURRENCY_FORMAT"
            "claimantName", "", "INPUT_TEXT_REQUIRED"
        ] do
        let input = input fixture field value

        for endpoint in [ "case.preview"; "case.execute" ] do
            let reply = automation fixture endpoint input 2

            Expect.equal
                (reply.GetProperty("kind").GetString())
                "result"
                "Malformed business input reaches the typed refusal"

            let rejection = require "REJECTED" reply

            Expect.equal
                (rejection.GetProperty("field").GetString())
                field
                "Field guidance is retained"

            Expect.equal
                (rejection.GetProperty("diagnostic").GetProperty("id").GetString())
                diagnostic
                "Application owns format refusal"

    for value, diagnostic in
        [ " ", "INPUT_TEXT_REQUIRED"; String.replicate 201 "x", "INPUT_TEXT_TOO_LONG" ] do
        let input = input fixture "claimantName" value

        for endpoint in [ "case.preview"; "case.execute" ] do
            let reply = automation fixture endpoint input 2

            Expect.equal
                (reply.GetProperty("kind").GetString())
                "result"
                "Domain owns current admission"

            let rejection = require "REJECTED" reply

            Expect.equal
                (rejection.GetProperty("diagnostic").GetProperty("id").GetString())
                diagnostic
                "Current policy diagnostic remains reachable"


let private principalRefusals fixture =
    for issuer, subject, target, issue in
        [
            "", "", "issuer", "MISSING"
            "https://issuer.example/", "", "subject", "MISSING"
            "secret-canary", "synthetic", "issuer", "INVALID"
        ] do
        let reply =
            automation
                fixture
                "authority.execute"
                {|
                    operationId = id ()
                    target =
                        {|
                            kind = "HUMAN"
                            issuer = issuer
                            subject = subject
                        |}
                    act = {| kind = "REGISTER_ACTOR" |}
                |}
                3

        let failure = reply.GetProperty("failure")
        Expect.equal (failure.GetProperty("status").GetInt32()) 400 "Technical input refusal"
        Expect.equal (failure.GetProperty("executionPhase").GetString()) "NOT_STARTED" "No dispatch"
        let parameters = failure.GetProperty("diagnostic").GetProperty("parameters")
        Expect.equal (parameters.GetProperty("target").GetString()) target "Core field guidance"
        Expect.equal (parameters.GetProperty("issue").GetString()) issue "Core cause"
        Expect.isFalse (reply.GetRawText().Contains "secret-canary") "No authored principal echo"


let qualify (fixture: PublishedFixture.Fixture) =
    let before = fixture.Administration [ "head" ]
    Expect.equal before.ExitCode 0 "Read accepted head before refusals"

    businessRefusals fixture
    principalRefusals fixture
    let after = fixture.Administration [ "head" ]
    Expect.equal after.ExitCode 0 "Read accepted head after refusals"
    Expect.equal after.StandardOutput before.StandardOutput "Refusals append nothing"

    let verified = fixture.Administration [ "verify" ]

    Expect.equal
        verified.ExitCode
        0
        "Refused attempts leave projections consistent with the unchanged ledger"
