module internal ClaimCore.AcceptanceTests.RejectedDrafts

open ClaimCore.Protocol
open ClaimCore.Domain
open ClaimCore.Json

open System.IO
open System.Text.Json
open System.Text.Json.Nodes
open Expecto
open CliCalls

let private input (fixture: PublishedFixture.Fixture) (field: string) value =
    let authored =
        JsonNode.Parse(File.ReadAllBytes(Path.Combine(fixture.Root, "examples/01-open.json")))
        |> nonNull

    let input = authored["input"] |> nonNull
    input["operationId"] <- JsonValue.Create(id ())
    (input["target"] |> nonNull)["caseReference"] <- JsonValue.Create("INVALID-" + id ())
    let values = (input["act"] |> nonNull)["values"] |> nonNull
    values[field] <- JsonValue.Create<string> value
    input

// Independent wire expectations: do not derive the oracle from the diagnostic codec.
let private issueParameters =
    function
    | "DECIMAL_FORMAT" -> [ "maximumIntegerDigits", 18; "maximumFractionalDigits", 4 ]
    | "TOO_LONG" -> [ "maximumCharacters", 200 ]
    | _ -> []

let private properties (value: JsonElement) =
    value.EnumerateObject() |> Seq.map _.Name |> Set.ofSeq

let private assertIssues expected (refusal: JsonElement) =
    Expect.equal (refusal.GetProperty("code").GetString()) "INVALID_INPUT" "Input refusal envelope"

    Expect.equal
        (refusal.GetProperty("action").GetString())
        "CORRECT_INPUT"
        "Every issue is repairable input"

    Expect.equal
        (properties refusal)
        (Set.ofList [ "code"; "action"; "issues" ])
        "No scalar target or unexpected envelope fields"

    let actual = refusal.GetProperty("issues").EnumerateArray() |> Seq.toList

    Expect.hasLength
        actual
        (List.length expected)
        "Every independent issue is reported exactly once"

    for issue, (field, code) in List.zip actual expected do
        Expect.equal
            (issue.GetProperty("field").GetString())
            field
            "Field guidance and issue order are retained"

        Expect.equal
            (issue.GetProperty("code").GetString())
            code
            "Domain owns the specific input cause"

        let parameters = issueParameters code
        let names = Set.ofList ([ "field"; "code" ] @ (parameters |> List.map fst))
        Expect.equal (properties issue) names "Only declared parameters accompany each issue"

        for name, value in parameters do
            Expect.equal
                (issue.GetProperty(name).GetInt32())
                value
                "Independent published input bound"

let private businessRefusals fixture =
    for field, value, diagnostic in
        [
            "incidentDate", "2026-02-30", "CALENDAR_DATE_REQUIRED"
            "claimedAmount", "١٢٫٥٠", "DECIMAL_FORMAT"
            "claimedAmount", "00.10", "DECIMAL_FORMAT"
            "claimedCurrency", "ĀBC", "CURRENCY_FORMAT"
            "claimantName", "", "TEXT_REQUIRED"
        ] do
        let input = input fixture field value

        for endpoint in [ "operation.preview"; "operation.execute" ] do
            let reply = automation fixture endpoint input 2

            Expect.equal
                (reply.GetProperty("delivery").GetString())
                "DELIVERED"
                "Malformed business input reaches the typed refusal"

            let rejection = require "REJECTED" reply

            assertIssues [ field, diagnostic ] rejection

    for value, diagnostic in [ " ", "TEXT_REQUIRED"; String.replicate 201 "x", "TOO_LONG" ] do
        let input = input fixture "claimantName" value

        for endpoint in [ "operation.preview"; "operation.execute" ] do
            let reply = automation fixture endpoint input 2

            Expect.equal
                (reply.GetProperty("delivery").GetString())
                "DELIVERED"
                "Domain owns current admission"

            let rejection = require "REJECTED" reply

            assertIssues [ "claimantName", diagnostic ] rejection


let private principalRefusals fixture =
    for issuer, subject, expected in
        [
            "", "", [ "principal.issuer", "TEXT_REQUIRED"; "principal.subject", "TEXT_REQUIRED" ]
            "https://issuer.example/", "", [ "principal.subject", "TEXT_REQUIRED" ]
            "secret-canary", "synthetic", [ "principal.issuer", "PRINCIPAL_FORMAT" ]
        ] do
        let reply =
            automation
                fixture
                "operation.execute"
                {|
                    kind = "AUTHORITY"
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
        assertIssues expected refusal
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
