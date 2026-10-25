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

        for endpoint in [ "operation.preview"; "operation.execute" ] do
            let reply = automation fixture endpoint input 2

            Expect.equal
                (reply.GetProperty("delivery").GetString())
                "DELIVERED"
                "Malformed business input reaches the typed refusal"

            let rejection = require "REJECTED" reply

            Expect.equal
                (rejection.GetProperty("target").GetString())
                field
                "Field guidance is retained"

            Expect.equal
                (rejection.GetProperty("code").GetString())
                diagnostic
                "Application owns format refusal"

    for value, diagnostic in
        [ " ", "INPUT_TEXT_REQUIRED"; String.replicate 201 "x", "INPUT_TEXT_TOO_LONG" ] do
        let input = input fixture "claimantName" value

        for endpoint in [ "operation.preview"; "operation.execute" ] do
            let reply = automation fixture endpoint input 2

            Expect.equal
                (reply.GetProperty("delivery").GetString())
                "DELIVERED"
                "Domain owns current admission"

            let rejection = require "REJECTED" reply

            Expect.equal
                (rejection.GetProperty("code").GetString())
                diagnostic
                "Current policy diagnostic remains reachable"


let private principalRefusals fixture =
    for issuer, subject, target, issue in
        [
            "", "", "principal.issuer", "INPUT_TEXT_REQUIRED"
            "https://issuer.example/", "", "principal.subject", "INPUT_TEXT_REQUIRED"
            "secret-canary", "synthetic", "principal.issuer", "INPUT_PRINCIPAL_FORMAT"
        ] do
        let reply =
            automation
                fixture
                "operation.execute"
                {|
                    scope = "AUTHORITY"
                    operationId = id ()
                    target =
                        {|
                            kind = "HUMAN"
                            issuer = issuer
                            subject = subject
                        |}
                    act = {| kind = "REGISTER" |}
                |}
                2

        let refusal = require "REJECTED" reply
        Expect.equal (refusal.GetProperty("target").GetString()) target "Application field guidance"
        Expect.equal (refusal.GetProperty("code").GetString()) issue "Application cause"
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
