module ClaimCore.Tests.WireRequestTests

open System
open System.IO
open System.Text
open System.Text.Json
open Expecto
open ClaimCore.Domain
open ClaimCore.Application.Contracts
open ClaimCore.Wire
open ClaimCore.Presentation
open ClaimCore.Json
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
                    Path.Combine(RepositoryRoot.find (), "tests/fixtures/examples/requests.json")
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

            let encoded = endpoint.NormalizeInput value |> accepted

            Expect.equal
                (CanonicalWriter.bytes encoded)
                (CanonicalWriter.bytes value)
                "Typed inputs round trip without semantic changes."

            seen <- Set.add (Api.id endpoint) seen
        else
            Expect.isError
                (admit endpoint raw)
                ("Negative authored request must be refused: " + text "id" example)

    Expect.equal
        seen
        (Api.all |> List.map Api.id |> Set.ofList)
        "Every registry request codec is exercised."

let private scalarAdmission () =
    let id = "10000000-0000-7000-8000-000000000001"

    let request =
        (OperationSubmissionCodecs.submission.Write
         << ClaimCore.TestSupport.Submissions.ofCommand None) (
            ClaimCore.TestSupport.Inputs.caseOperation
                {
                    OperationId = Guid.Parse id
                    CaseReference = ""
                    ExpectedRevision = 0L
                    Act =
                        AuthoredCaseAct.Open(
                            Inputs.registrationWith
                                [
                                    BusinessField.IncidentDate, "not-a-date"
                                    BusinessField.IncidentNotificationDate, ""
                                    BusinessField.IncidentCountry, "lowercase"
                                    BusinessField.ClaimantName, ""
                                    BusinessField.InsurerName, ""
                                    BusinessField.ClaimedAmount, "not-money"
                                    BusinessField.ClaimedCurrency, "eur"
                                ]
                                Inputs.registration
                        )
                }
        )
        |> WireWriter.bytes
        |> Encoding.UTF8.GetString

    Expect.isOk
        (admit (endpoint "operation.execute") request)
        "Business grammar belongs to Application"

    for reference in [ ""; "not a reference" ] do
        let raw =
            JsonSerializer.Serialize
                {|
                    target =
                        {|
                            kind = "REFERENCE"
                            caseReference = reference
                        |}
                |}

        Expect.isOk
            (admit (endpoint "case.get") raw)
            "Read reference format also reaches Application"

    let voided =
        $"""{{"operationId":"{id}","task":"case.void","binding":{{"kind":"CASE","target":{{"kind":"REFERENCE","caseReference":"CASE"}},"expectedRevision":1}},"inputs":{{"input:reason":""}}}}"""

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
            "{\"limit\":1,\"cursor\":\""
            + String('A', Paging.MaximumCaseCursorCharacters + 1)
            + "\"}"
        ] do
        Expect.isError
            (admit (endpoint "case.list") raw)
            "Technical shape and cursor length are closed"

    let raw =
        """{"operationId":"00000000-0000-0000-0000-000000000000","task":"case.erase","binding":{"kind":"CASE","target":{"kind":"REFERENCE","caseReference":"CASE"},"expectedRevision":0},"inputs":{}}"""

    Expect.isOk
        (admit
            (endpoint "operation.execute")
            (raw.Replace(
                "00000000-0000-0000-0000-000000000000",
                "10000000-0000-7000-8000-000000000001"
            )))
        "Changing only the UUID admits this scoped request."

    Expect.isError
        (admit (endpoint "operation.execute") raw)
        "Operation identity is lexical admission"

    Expect.isError
        (admit (endpoint "session") "{\"extra\":true}")
        "Empty endpoint inputs are closed objects"

let private registrationClear () =
    let raw =
        """{"operationId":"10000000-0000-7000-8000-000000000001","task":"case.correct","binding":{"kind":"CASE","target":{"kind":"REFERENCE","caseReference":"CASE"},"expectedRevision":1},"inputs":{"group:registration":"CLEAR","group:decision":"KEEP"}}"""

    Expect.isError
        (Codec.decode
            Limits.MaximumRequestBytes
            OperationSubmissionCodecs.submission
            (Encoding.UTF8.GetBytes raw))
        "Registration clearing is outside the declared correction actions"

[<Tests>]
let tests =
    testList
        "typed wire requests"
        [
            testCase "independent examples cover every registry endpoint codec" examples
            testCase "business scalar grammar is not shape admission" scalarAdmission
            testCase "technical structures remain closed" structure
            testCase "registration clearing is rejected by the wire shape" registrationClear
        ]
