module ClaimCore.WebTests.HostTransportTests

open ClaimCore.Wire

open System
open System.Net.Http
open System.Text
open System.Text.Json
open System.Threading
open System.Threading.Tasks
open Expecto
open ClaimCore.Application
open ClaimCore.Web
open ClaimCore.WebTests.FacadeStub
open ClaimCore.WebTests.TestServerFixture

let private body name =
    if name = "case.preview" || name = "case.execute" then
        Array.copy commandInput
    elif name = "authority.execute" then
        Encoding.UTF8.GetBytes(
            $"""{{"operationId":"{operationId}","target":{{"kind":"HUMAN","issuer":"https://issuer.example.test/realm","subject":"person"}},"act":{{"kind":"REGISTER_ACTOR"}}}}"""
        )
    elif name = "session" || name = "session.logout" || name = "context" then
        Encoding.UTF8.GetBytes "{}"
    else
        let root = ClaimCore.TestSupport.RepositoryRoot.find ()

        use document =
            JsonDocument.Parse(
                System.IO.File.ReadAllBytes(
                    System.IO.Path.Combine(root, "tests/fixtures/wire-request-examples.json")
                )
            )

        document.RootElement.GetProperty("cases").EnumerateArray()
        |> Seq.find (fun item -> item.GetProperty("endpoint").GetString() = name)
        |> fun item -> item.GetProperty("value").GetRawText() |> Encoding.UTF8.GetBytes

let private parse bytes = JsonDocument.Parse(bytes: byte array)

let private loggedIn ready work =
    task {
        use host = new Host(ready)
        let! status, _ = host.Login()
        Expect.equal status 200 "Synthetic cookie login"
        let! token = host.Token()
        do! work host token
    }
    |> _.GetAwaiter().GetResult()

let private actorTarget endpoint (response: byte array) =
    if endpoint = "authority.grants" then
        use request = JsonDocument.Parse(ReadOnlyMemory(body endpoint))
        use result = JsonDocument.Parse(ReadOnlyMemory response)

        Expect.equal
            (result.RootElement
                .GetProperty("outcome")
                .GetProperty("data")
                .GetProperty("actor")
                .GetProperty("actorId")
                .GetString())
            (request.RootElement.GetProperty("actorId").GetString())
            "Typed grant dispatch retains the requested target"

let private catalogueRoutes () =
    loggedIn true (fun host token ->
        task {
            let! status, bootstrap = host.Bootstrap()
            Expect.equal status 200 "Bootstrap GET"

            Expect.isOk
                (Replies.decode ClaimCore.Wire.Endpoint.Session bootstrap)
                "Bootstrap schema"

            for endpoint in
                Endpoint.all
                |> List.filter (fun e ->
                    (Endpoint.id e) <> "session" && (Endpoint.id e) <> "session.logout") do
                let! status, response =
                    host.Call((Endpoint.id endpoint), body (Endpoint.id endpoint), token)

                Expect.equal status 200 (Endpoint.id endpoint)

                Expect.isOk (Replies.decode endpoint response) "Typed response schema"

                actorTarget (Endpoint.id endpoint) response

            Expect.equal host.Facade.Calls.Length 10 "Every core endpoint exactly once"
            let! status, response = host.Call("session.logout", body "session.logout", token)
            Expect.equal status 200 "Logout"

            Expect.isOk
                (Replies.decode ClaimCore.Wire.Endpoint.SessionLogout response)
                "Logout schema"

            let! status, _ = host.Call("context", body "context", token)
            Expect.equal status 401 "Revoked cookie cannot call core"
        })

let private handshakeRefusals () =
    loggedIn true (fun host token ->
        task {
            for fingerprint in [ ""; String.replicate 64 "0" ] do
                let! status, response =
                    host.Call(
                        "case.execute",
                        body "case.execute",
                        token,
                        fingerprint = fingerprint
                    )

                Expect.equal status 409 "Contract refusal"
                use payload = parse response

                Expect.equal
                    (payload.RootElement.GetProperty("code").GetString())
                    "CONTRACT_MISMATCH"
                    "Typed host failure"

            let! status, _ =
                host.Send(
                    HttpMethod.Post,
                    "/api/case.execute",
                    Some(body "case.execute"),
                    [
                        "Origin", origin.GetLeftPart(UriPartial.Authority)
                        "X-ClaimCore-Antiforgery", token
                    ]
                )

            Expect.equal status 409 "Missing header"
            Expect.isEmpty host.Facade.Calls "No factory/core dispatch on mismatch"
            Expect.isNone host.Facade.Principal "No actor factory on mismatch"
            let! status, _ = host.Call("case.execute", body "case.execute", token)
            Expect.equal status 200 "Matching header"
            Expect.equal host.Facade.Calls [ "case.execute" ] "One matching dispatch"
        })

let private admissionRefusals () =
    loggedIn true (fun host token ->
        task {
            let! status, _ = host.Call("case.execute", body "case.execute", "incorrect")
            Expect.equal status 403 "Antiforgery"

            let! status, _ =
                host.Call(
                    "case.get",
                    Encoding.UTF8.GetBytes "{\"caseReference\":\"WEB-001\",\"extra\":true}",
                    token
                )

            Expect.equal status 400 "Exact shape"
            let! status, _ = host.Send(HttpMethod.Get, "/api/case.get", None, [])
            Expect.equal status 405 "Wrong method"

            let! status, _ = host.Send(HttpMethod.Post, "/API/context", Some(body "context"), [])

            Expect.equal status 404 "No path aliases"

            let! status, _ =
                host.Send(HttpMethod.Post, "/api/unsupported.endpoint", Some [||], [])

            Expect.equal status 404 "Absent endpoint"
            Expect.isEmpty host.Facade.Calls "Refusing guards never call core"
        })

let private outcomes () =
    loggedIn true (fun host token ->
        task {
            let accepted = receipt operationId "WEB-001"

            let values =
                [
                    ExecuteOutcome.Accepted accepted, "ACCEPTED"
                    ExecuteOutcome.AlreadyAccepted accepted, "ALREADY_ACCEPTED"
                    ExecuteOutcome.Rejected(
                        Rejection.Case(ClaimCore.Domain.CaseRefusal.CaseVoided)
                    ),
                    "REJECTED"
                    ExecuteOutcome.Failed CoreFault.StoreUnavailable, "FAILED"
                    ExecuteOutcome.Cancelled, "CANCELLED"
                    ExecuteOutcome.OutcomeUnknown operationId, "OUTCOME_UNKNOWN"
                ]

            for value, tag in values do
                host.Facade.Execute <-
                    (fun (_, ct) ->
                        Expect.equal
                            ct
                            CancellationToken.None
                            "Dispatched mutations are not request-cancellable"

                        Task.FromResult value)

                let! status, response = host.Call("case.execute", body "case.execute", token)
                Expect.equal status 200 "Outcomes stay typed"
                Expect.isOk (Replies.decode ClaimCore.Wire.Endpoint.CaseExecute response) "Schema"
                use payload = parse response

                Expect.equal
                    (payload.RootElement.GetProperty("operationId").GetGuid())
                    operationId
                    "Every outcome, including refusal, echoes admitted request identity"

                Expect.equal
                    (payload.RootElement.GetProperty("outcome").GetProperty("tag").GetString())
                    tag
                    "Outcome identity"
        })

let private authorityCancellation () =
    loggedIn true (fun host token ->
        task {
            host.Facade.ExecuteAuthority <-
                fun (request, ct) ->
                    Expect.equal
                        ct
                        CancellationToken.None
                        "Authority mutations are not request-cancellable"

                    Task.FromResult(ExecuteOutcome.Accepted(authorityReceipt request))

            let! status, response =
                host.Call("authority.execute", body "authority.execute", token)

            Expect.equal status 200 "Authority dispatch"

            Expect.isOk
                (Replies.decode ClaimCore.Wire.Endpoint.AuthorityExecute response)
                "Authority response"
        })

let private malformedWire () =
    loggedIn true (fun host token ->
        task {
            let source = Encoding.UTF8.GetString commandInput

            let changes =
                [
                    for value in [ "-1"; "9007199254740992"; "1.0"; "1e0"; "\"1\"" ] do
                        yield "\"expectedRevision\":0", "\"expectedRevision\":" + value
                    yield "Synthetic claimant", "bad\\u0000name"
                    yield "Synthetic claimant", "bad\\ufdd0name"
                    yield "Synthetic claimant", "bad\\ud800name"
                ]

            for before, after in changes do
                let changed = source.Replace(before, after)
                Expect.notEqual changed source "Negative control changes actual bytes"
                let! status, _ = host.Call("case.execute", Encoding.UTF8.GetBytes changed, token)
                Expect.equal status 400 "Permanent malformed format refuses before dispatch"

            Expect.isEmpty host.Facade.Calls "Malformed wire never reaches Application"
        })

let private businessFormatRefusals () =
    task {
        use! fixture = OidcCoreFixture.createFor oidc.Issuer.AbsoluteUri "synthetic-human"
        use host = new Host(true, forActor = fixture.Actor)
        let! _ = host.Login()
        let! token = host.Token()
        let source = Encoding.UTF8.GetString commandInput

        for before, after, field in
            [
                "\"LV\"", "\"Latvia\"", "incidentCountry"
                "\"WEB-001\"", "\"a b\"", "caseReference"
                "\"WEB-001\"", "\"é\"", "caseReference"
                "2026-08-01", "not-a-date", "incidentDate"
                "\"1.00\"", "\"one\"", "claimedAmount"
                "\"EUR\"", "\"euros\"", "claimedCurrency"
            ] do
            let changed = source.Replace(before, after)
            Expect.notEqual changed source "Control changes the authored input."

            let! status, response =
                host.Call("case.execute", Encoding.UTF8.GetBytes changed, token)

            Expect.equal status 200 "Business format refusal is a typed outcome."

            match Replies.decode Endpoint.CaseExecute response with
            | Ok(WireReply.CaseExecute {
                                           Outcome = CaseExecuteOutcome.Rejected value
                                       }) ->
                Expect.equal value.Field (Some field) "The malformed business field is identified."
            | _ -> failtest "Malformed business format must produce a typed rejection."
    }
    |> _.GetAwaiter().GetResult()

let private readiness () =
    for ready, status in [ true, 200; false, 503 ] do
        use host = new Host(ready)

        for path, expected in [ "/health/live", 200; "/health/ready", status ] do
            let actual, _ = host.Send(HttpMethod.Get, path, None, []).GetAwaiter().GetResult()
            Expect.equal actual expected "Process and database conditions are separate"

        let actual, _ =
            host.Send(HttpMethod.Post, "/health/ready", None, []).GetAwaiter().GetResult()

        Expect.equal actual 405 "Readiness is GET only"
        Expect.isEmpty host.Facade.Calls "Health has no actor facade"

let private principalAdmission () =
    loggedIn true (fun host token ->
        task {
            let target kind issuer subject =
                System.Text.Json.JsonSerializer.SerializeToUtf8Bytes
                    {|
                        operationId = operationId
                        target =
                            {|
                                kind = kind
                                issuer = issuer
                                subject = subject
                            |}
                        act = {| kind = "REGISTER_ACTOR" |}
                    |}

            let samples =
                [
                    "HUMAN", "https://ISSUER.example.test/realm", "person"
                    "HUMAN", "https://issuer.example.test/realm?x=1", "person"
                    "HUMAN", "https://user@issuer.example.test/realm", "person"
                    "HUMAN", "https://issuer.example.test/realm#part", "person"
                    "HUMAN", "https://issuer.example.test/realm", " "
                    "HUMAN", "https://issuer.example.test/realm", String.replicate 257 "🙂"
                    "SERVICE", "https://issuer.example.test/realm", String.replicate 257 "x"
                    "HUMAN", "https://issuer.example.test/" + String.replicate 2048 "x", "person"
                ]

            for kind, issuer, subject in samples do
                let input = target kind issuer subject

                let admitted =
                    ClaimCore.Wire.Admission.admit Endpoint.AuthorityExecute input |> Result.isOk

                Expect.equal admitted true "Generic wire syntax does not claim identity admission"

                let! status, response = host.Call("authority.execute", input, token)
                Expect.equal status 400 "Typed principal refusal occurs before core dispatch"
                use failure = JsonDocument.Parse response

                Expect.equal
                    (failure.RootElement.GetProperty("code").GetString())
                    "INPUT_INVALID"
                    "Defined failure"

                Expect.equal
                    (failure.RootElement.GetProperty("executionPhase").GetString())
                    "NOT_STARTED"
                    "No dispatch"

            Expect.isEmpty host.Facade.Calls "Identity failure never invokes the actor core"
            Expect.isNone host.Facade.Principal "Identity failure never creates an actor facade"
        })

[<Tests>]
let tests =
    testList
        "HTTP service"
        [
            testCase
                "generic wire principal syntax is separate from authoritative pre-dispatch admission"
                principalAdmission
            testCase
                "authority execution preserves mutation cancellation semantics"
                authorityCancellation
            testCase "raw malformed numeric and unsafe Unicode inputs cannot dispatch" malformedWire
            testCase
                "business scalar formats reject through real Application after HTTP dispatch"
                businessFormatRefusals
            testCase "all twelve endpoints use the one catalogue and typed facade" catalogueRoutes
            testCase
                "matching contract dispatches; missing and different contracts never dispatch"
                handshakeRefusals
            testCase
                "antiforgery, shapes, methods and literal paths have refusing controls"
                admissionRefusals
            testCase
                "every execute outcome reaches HTTP without cancellation reclassification"
                outcomes
            testCase "live and ready are exact read-only operational probes" readiness
        ]
    |> testSequenced
