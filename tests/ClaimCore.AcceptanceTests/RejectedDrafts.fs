module internal ClaimCore.AcceptanceTests.RejectedDrafts

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

let qualify (fixture: PublishedFixture.Fixture) =
    let before = fixture.Administration [ "head" ]
    Expect.equal before.ExitCode 0 "Read accepted head before refusals"

    for field, value in
        [
            "incidentDate", "2026-02-30"
            "claimedAmount", "١٢٫٥٠"
            "claimedCurrency", "ĀBC"
            "claimantName", ""
        ] do
        let input = input fixture field value

        for endpoint in [ "case.preview"; "case.execute" ] do
            let reply = automation fixture endpoint input 2

            Expect.equal
                (reply.GetProperty("kind").GetString())
                "protocolFailure"
                "Permanent malformed format refuses before transport"

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

    let after = fixture.Administration [ "head" ]
    Expect.equal after.ExitCode 0 "Read accepted head after refusals"
    Expect.equal after.StandardOutput before.StandardOutput "Refusals append nothing"
