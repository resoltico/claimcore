module ClaimCore.Tests.WireRequestTests

open System
open System.IO
open System.Text
open System.Text.Json
open Expecto
open ClaimCore.Contract
open ClaimCore.Json
open ClaimCore.Contract.Protocol
open ClaimCore.TestSupport

let private text name (value: JsonElement) =
    value.GetProperty(name: string).GetString() |> nonNull

let private admit endpoint (raw: string) =
    StrictJson.read Limits.MaximumRequestBytes (Encoding.UTF8.GetBytes raw)
    |> Result.bind (Api.admitInput endpoint)

let private endpoint name = Api.tryFind name |> Option.get

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
        let endpoint = Api.tryFind (text "endpoint" example) |> Option.get
        let raw = example.GetProperty("value").GetRawText()

        if example.GetProperty("valid").GetBoolean() then
            admit endpoint raw |> accepted |> ignore

            let value =
                StrictJson.read Limits.MaximumRequestBytes (Encoding.UTF8.GetBytes raw)
                |> accepted

            let encoded =
                endpoint.Accept
                    { new IEndpointVisitor<JsonValue> with
                        member _.Visit endpoint =
                            endpoint.Input.Read "$" value |> accepted |> endpoint.Input.Write
                    }

            Expect.equal
                (CanonicalWriter.bytes encoded)
                (CanonicalWriter.bytes value)
                "Typed inputs round trip without semantic changes."

            seen <- Set.add (Api.id endpoint) seen
        else
            Expect.isError (admit endpoint raw) "Negative authored request must be refused"

    Expect.equal
        seen
        (Api.all |> List.map Api.id |> Set.ofList)
        "Every registry request codec is exercised."

let private scalarAdmission () =
    let id = "10000000-0000-4000-8000-000000000001"

    let request =
        $"{{\"kind\":\"CASE\",\"operationId\":\"{id}\",\"caseReference\":\"\",\"expectedRevision\":0,\"act\":{{\"kind\":\"OPEN\",\"values\":{{\"incidentDate\":\"not-a-date\",\"incidentNotificationDate\":\"\",\"incidentCountry\":\"lowercase\",\"claimantName\":\"\",\"insurerName\":\"\",\"claimedAmount\":\"not-money\",\"claimedCurrency\":\"eur\"}}}}}}"

    Expect.isOk
        (admit (endpoint "operation.execute") request)
        "Business grammar belongs to Application"

    for reference in [ ""; "not a reference" ] do
        let raw = JsonSerializer.Serialize {| caseReference = reference |}

        Expect.isOk
            (admit (endpoint "case.get") raw)
            "Read reference format also reaches Application"

    let voided =
        $"{{\"kind\":\"CASE\",\"operationId\":\"{id}\",\"caseReference\":\"CASE\",\"expectedRevision\":1,\"act\":{{\"kind\":\"VOID\",\"reason\":\"\"}}}}"

    Expect.isOk
        (admit (endpoint "operation.preview") voided)
        "Empty authored reason reaches Application"

let private structure () =
    for raw in
        [
            "{\"limit\":1,\"cursor\":null}"
            "{\"limit\":51}"
            "{\"limit\":1,\"extra\":true}"
            "{\"limit\":1,\"cursor\":\"\"}"
            "{\"limit\":1,\"cursor\":\"" + String('A', 513) + "\"}"
        ] do
        Expect.isError
            (admit (endpoint "case.list") raw)
            "Technical shape and cursor length are closed"

    let raw =
        "{\"kind\":\"CASE\",\"operationId\":\"00000000-0000-0000-0000-000000000000\",\"caseReference\":\"CASE\",\"expectedRevision\":0,\"act\":{\"kind\":\"ERASE\"}}"

    Expect.isOk
        (admit
            (endpoint "operation.execute")
            (raw.Replace(
                "00000000-0000-0000-0000-000000000000",
                "10000000-0000-4000-8000-000000000001"
            )))
        "Changing only the UUID admits this scoped request."

    Expect.isError
        (admit (endpoint "operation.execute") raw)
        "Operation identity is lexical admission"

    Expect.isError
        (admit (endpoint "session") "{\"extra\":true}")
        "Empty endpoint inputs are closed objects"

[<Tests>]
let tests =
    testList
        "typed wire requests"
        [
            testCase "independent examples cover every registry endpoint codec" examples
            testCase "business scalar grammar is not shape admission" scalarAdmission
            testCase "technical structures remain closed" structure
        ]
