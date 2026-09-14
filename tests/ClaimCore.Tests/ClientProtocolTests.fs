module ClaimCore.Tests.ClientProtocolTests

open System
open System.Text
open Expecto
open ClaimCore.Protocol
open ClientProtocolFixtures

let private exactDecision () =
    let value = decoded WebV2.commandPrepare.Request decision
    Expect.equal value.ExpectedRevision "9007199254740993" "Revision never passes through binary64"

    match value.Command with
    | CommandPrepareRequestCommand.Decide proposal ->
        Expect.equal proposal.Values.PayableAmount "0.00" "Authored decimal spelling is preserved"
        Expect.equal proposal.Values.PayableCurrency "EUR" "Currency remains coupled input"
    | _ -> failtest "Wrong command alternative"

    jsonEqual (utf8 decision) (encoded WebV2.commandPrepare.Request value)

let private money () =
    let source amount =
        decision.Replace("0.00", amount, StringComparison.Ordinal)

    for amount in [ "0"; "1.0000"; "999999999999999999.9999" ] do
        decoded WebV2.commandPrepare.Request (source amount) |> ignore

    for amount in [ "-1"; "1e3"; "1.00001"; "1000000000000000000"; "01"; "1,25" ] do
        refused WebV2.commandPrepare.Request (source amount)

let private dates () =
    for date in [ "0001-01-01"; "9999-12-31"; "2024-02-29" ] do
        decoded
            WebV2.commandPrepare.Request
            (decision.Replace("2024-02-29", date, StringComparison.Ordinal))
        |> ignore

    for date in [ "0000-01-01"; "2026-02-29"; "2026-13-01"; "2026-1-01" ] do
        refused
            WebV2.commandPrepare.Request
            (decision.Replace("2024-02-29", date, StringComparison.Ordinal))

let private revisions () =
    let source value =
        decision.Replace("9007199254740993", value, StringComparison.Ordinal)

    for value in [ "0"; "9223372036854775806" ] do
        decoded WebV2.commandPrepare.Request (source value) |> ignore

    for value in [ "9223372036854775807"; "-1"; "01"; "1e2" ] do
        refused WebV2.commandPrepare.Request (source value)

let private unicode () =
    let emoji = Char.ConvertFromUtf32(0x1F642)

    let valid =
        {
            CaseReference = String.replicate 80 emoji
        }

    let bytes = encoded WebV2.caseGet.Request valid
    let actual = WebV2.caseGet.Request.Decode(maximum, bytes)
    Expect.equal actual (Ok valid) "Scalar count is not UTF-16 code-unit count"

    Expect.isError
        (WebV2.caseGet.Request.Encode(
            maximum,
            {
                CaseReference = String.replicate 81 emoji
            }
        ))
        "81 scalars exceed reference bound"

    for text in [ "\u00a0SYNTHETIC"; "SYNTHETIC\u00a0"; "SYNTHETIC\u0000"; "\u0085" ] do
        Expect.isError
            (WebV2.caseGet.Request.Encode(maximum, { CaseReference = text }))
            "Whitespace and controls are rejected without rewriting"

let private framing () =
    for text in
        [
            ""
            "{} {}"
            "{\"caseReference\":\"A\",}"
            "/* comment */{}"
            "{\"caseReference\":\"A\",\"caseReference\":\"B\"}"
            "{\"caseReference\":\"A\",\"case\\u0052eference\":\"B\"}"
            "{\"caseReference\":\"\\ud800\"}"
            "{\"caseReference\":\"\\udc00\"}"
            "{\"caseReference\":\"A\",\"unexpected\":\"\\ud800\"}"
            String.replicate 65 "[" + "0" + String.replicate 65 "]"
        ] do
        refused WebV2.caseGet.Request text

    for bytes in [ [| 0xffuy |]; Array.append [| 0xefuy; 0xbbuy; 0xbfuy |] (utf8 "{}") ] do
        Expect.isError
            (WebV2.caseGet.Request.Decode(maximum, bytes))
            "Invalid original byte framing is rejected"

let private byteBudget () =
    let bytes = utf8 "{\"caseReference\":\"SYNTHETIC-1\"}"

    let value =
        WebV2.caseGet.Request.Decode(bytes.Length, bytes)
        |> function
            | Ok value -> value
            | Error _ -> failtest "Exact budget"

    Expect.isError (WebV2.caseGet.Request.Decode(bytes.Length - 1, bytes)) "Oversize decode"

    Expect.equal
        (WebV2.caseGet.Request.Encode(bytes.Length, value))
        (Ok bytes)
        "Exact encode budget"

    Expect.isError (WebV2.caseGet.Request.Encode(bytes.Length - 1, value)) "Oversize encode"

let private presence () =
    let absent = decoded WebV2.caseList.Request "{\"limit\":1}"
    Expect.equal absent.Cursor None "Omitted cursor remains absent"
    jsonEqual (utf8 "{\"limit\":1}") (encoded WebV2.caseList.Request absent)
    refused WebV2.caseList.Request "{\"limit\":1,\"cursor\":null}"
    refused WebV2.caseList.Request "{\"limit\":1,\"cursor\":\"\"}"
    let value = decoded WebV2.caseGet.Response current
    jsonEqual (utf8 current) (encoded WebV2.caseGet.Response value)

    refused
        WebV2.caseGet.Response
        (current.Replace("\"paymentDate\":null,", "", StringComparison.Ordinal))

let private alteredTag () =
    let proposal = decoded WebV2.commandPrepare.Request decision

    let changed =
        { proposal with
            Command = CommandPrepareRequestCommand.Close { Kind = "REOPEN"; Values = () }
        }

    Expect.isError
        (WebV2.commandPrepare.Request.Encode(maximum, changed))
        "An authored union case cannot encode a different command"

    let malformed =
        {
            CaseReference = String([| char 0xD800 |])
        }

    Expect.isError
        (WebV2.caseGet.Request.Encode(maximum, malformed))
        "Encoding cannot replace malformed UTF-16"

let private endpointCorrelation () =
    refused WebV2.caseList.Response current

    refused
        WebV2.caseGet.Response
        (current.Replace("SUCCEEDED", "UNSUPPORTED", StringComparison.Ordinal))

    refused
        WebV2.caseGet.Response
        (current.Replace(
            "\"tag\":\"SUCCEEDED\"",
            "\"tag\":\"FAILED\",\"ta\\u0067\":\"SUCCEEDED\"",
            StringComparison.Ordinal
        ))

let private headers () =
    let endpoint = WebV2.recoveryImportEnvelopeRetain

    let value =
        decoded
            endpoint.Headers
            ("{\"X-ClaimCore-Source-Sha256\":\"" + String.replicate 64 "a" + "\"}")

    encoded endpoint.Headers value |> ignore
    refused endpoint.Headers "{}"
    refused endpoint.Headers "{\"X-ClaimCore-Source-Sha256\":\"ABC\"}"

    Expect.equal
        endpoint.Description.Body
        (RequestBody.Raw(
            "application/vnd.claimcore.recovery+json",
            131072,
            [ "X-ClaimCore-Source-Sha256" ]
        ))
        "Raw transport limits and header names remain generated"

let private numericIntegers () =
    for value in [ "1"; "1.0"; "1e0"; "10e-1" ] do
        let decoded = decoded WebV2.caseList.Request ("{\"limit\":" + value + "}")
        Expect.equal decoded.Limit 1L "JSON integer equivalence is exact"

    for value in [ "1.00000000000000000000000000000000001"; "1e-1000"; "1e1000"; "1.1" ] do
        refused WebV2.caseList.Request ("{\"limit\":" + value + "}")

let private encodeBudget () =
    let value =
        {
            CaseReference = String.replicate 10000 "x"
        }

    Expect.isError
        (WebV2.caseGet.Request.Encode(32, value))
        "Writer reservations cannot grow without a budget"

    Expect.isError
        (WebV2.sessionLogout.Request.Encode(1, ()))
        "Tiny budgets still check actual bytes"

    Expect.equal
        (WebV2.sessionLogout.Request.Encode(2, ()))
        (Ok(utf8 "{}"))
        "Tiny exact budget is not confused with buffer reservation"

let tests =
    testList
        "client protocol independent examples"
        [
            testCase "numeric integer decoding cannot round fractional input" numericIntegers
            testCase "encoder memory and tiny output budgets remain bounded" encodeBudget
            testCase "exact decimal text and large revisions survive typed round trip" exactDecision
            testCase "money input is exact and bounded" money
            testCase "real calendar boundaries are enforced" dates
            testCase "revision bounds do not lose precision" revisions
            testCase "Unicode scalar limits match the public text meaning" unicode
            testCase "original JSON framing rejects ambiguity and malformed Unicode" framing
            testCase "exact byte budgets apply to decoding and encoding" byteBudget
            testCase "omission differs from required null" presence
            testCase
                "encoders cannot change an authored alternative or replace malformed text"
                alteredTag
            testCase "response endpoint and alternative identities are checked" endpointCorrelation
            testCase "raw artifact headers and framing remain distinct from JSON payloads" headers
        ]
