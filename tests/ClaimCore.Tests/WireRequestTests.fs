module ClaimCore.Tests.WireRequestTests

open System
open System.IO
open System.Text
open System.Text.Json
open Expecto
open ClaimCore.Wire
open ClaimCore.TestSupport

let private text name (value: JsonElement) =
    value.GetProperty(name: string).GetString() |> nonNull

let private admit endpoint (raw: string) =
    Admission.admit endpoint (Encoding.UTF8.GetBytes raw)

let private accepted =
    function
    | Ok value -> value
    | Error _ -> failtest "Wire request unexpectedly refused"

let private examples () =
    use fixture =
        JsonDocument.Parse(
            File
                .ReadAllBytes(
                    Path.Combine(
                        RepositoryRoot.find (),
                        "tests/fixtures/wire-request-examples.json"
                    )
                )
                .AsMemory()
        )

    let examples = fixture.RootElement.GetProperty "cases"
    let mutable seen = Set.empty

    for example in examples.EnumerateArray() do
        let endpoint = Endpoint.tryParse (text "endpoint" example) |> Option.get
        let value = admit endpoint (example.GetProperty("value").GetRawText()) |> accepted
        Expect.equal (WireRequest.endpoint value) endpoint "Typed endpoint identity"

        Expect.equal
            (Admission.admit endpoint (Admission.encode value))
            (Ok value)
            "Every request DTO round trips"

        seen <- Set.add endpoint seen

    Expect.equal seen (Set.ofList Endpoint.all) "All twelve request codecs are exercised"

let private scalarAdmission () =
    let id = "10000000-0000-4000-8000-000000000001"

    let request =
        $"{{\"operationId\":\"{id}\",\"caseReference\":\"\",\"expectedRevision\":0,\"act\":{{\"kind\":\"OPEN\",\"values\":{{\"incidentDate\":\"not-a-date\",\"incidentNotificationDate\":\"\",\"incidentCountry\":\"lowercase\",\"claimantName\":\"\",\"insurerName\":\"\",\"claimedAmount\":\"not-money\",\"claimedCurrency\":\"eur\"}}}}}}"

    Expect.isOk (admit Endpoint.CaseExecute request) "Business grammar belongs to Application"

    for reference in [ ""; "not a reference" ] do
        let raw = JsonSerializer.Serialize {| caseReference = reference |}
        Expect.isOk (admit Endpoint.CaseGet raw) "Read reference format also reaches Application"

    let voided =
        $"{{\"operationId\":\"{id}\",\"caseReference\":\"CASE\",\"expectedRevision\":1,\"act\":{{\"kind\":\"VOID\",\"reason\":\"\"}}}}"

    Expect.isOk (admit Endpoint.CasePreview voided) "Empty authored reason reaches Application"

let private structure () =
    for raw in
        [
            "{\"limit\":1,\"cursor\":null}"
            "{\"limit\":51}"
            "{\"limit\":1,\"extra\":true}"
            "{\"limit\":1,\"cursor\":\"\"}"
            "{\"limit\":1,\"cursor\":\"" + String('A', 513) + "\"}"
        ] do
        Expect.isError (admit Endpoint.CaseList raw) "Technical shape and cursor length are closed"

    let raw =
        "{\"operationId\":\"00000000-0000-0000-0000-000000000000\",\"caseReference\":\"CASE\",\"expectedRevision\":0,\"act\":{\"kind\":\"ERASE\"}}"

    Expect.isError (admit Endpoint.CaseExecute raw) "Operation identity is lexical admission"

    Expect.isError
        (admit Endpoint.Session "{\"extra\":true}")
        "Empty endpoint inputs are closed objects"

[<Tests>]
let tests =
    testList
        "typed wire requests"
        [
            testCase "independent examples cover all twelve endpoint codecs" examples
            testCase "business scalar grammar is not shape admission" scalarAdmission
            testCase "technical structures remain closed" structure
        ]
