module internal ClaimCore.AcceptanceTests.RejectedDrafts

open System.IO
open System.Text.Json.Nodes
open Expecto
open CliCalls

let qualify (fixture: PublishedFixture.Fixture) =
    let before = fixture.Administration [ "head" ]
    Expect.equal before.ExitCode 0 "Read the accepted head before invalid drafts"

    for field, value, diagnostic in
        [
            "incidentDate", "2026-02-30", "INPUT_CALENDAR_DATE_REQUIRED"
            "claimedAmount", "١٢٫٥٠", "INPUT_DECIMAL_FORMAT"
            "claimedCurrency", "ĀBC", "INPUT_CURRENCY_FORMAT"
            "claimantName", "", "INPUT_TEXT_REQUIRED"
            "claimantName", String.replicate 201 "x", "INPUT_TEXT_TOO_LONG"
        ] do
        let authored =
            JsonNode.Parse(File.ReadAllBytes(Path.Combine(fixture.Root, "examples/01-open.json")))
            |> nonNull

        let input = authored["input"] |> nonNull
        input["operationId"] <- JsonValue.Create(id ())
        input["caseReference"] <- JsonValue.Create("INVALID-" + id ())
        let values = (input["command"] |> nonNull)["values"] |> nonNull
        values[field] <- JsonValue.Create<string> value

        for endpoint in [ "command.preview"; "command.execute" ] do
            let reply = automation fixture endpoint input 2
            Expect.equal (reply.GetProperty("kind").GetString()) "result" "Domain owns the refusal"
            let rejection = require "REJECTED" reply

            Expect.equal
                (rejection.GetProperty("diagnostic").GetProperty("id").GetString())
                diagnostic
                "Existing Domain diagnostic remains reachable"

    let after = fixture.Administration [ "head" ]
    Expect.equal after.ExitCode 0 "Read the accepted head after invalid drafts"
    Expect.equal after.StandardOutput before.StandardOutput "No invalid draft appends a ledger row"
