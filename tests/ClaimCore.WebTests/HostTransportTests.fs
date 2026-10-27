module ClaimCore.WebTests.HostTransportTests

open ClaimCore.Protocol
open ClaimCore.Domain
open ClaimCore.Json

open System
open System.Net.Http
open System.Text
open System.Text.Json
open System.Threading
open System.Threading.Tasks
open Expecto
open ClaimCore.Application
open ClaimCore.Web
open ClaimCore.TestSupport.HandlerStub
open ClaimCore.TestSupport.TestServerFixture

let private authorityBody =
    Encoding.UTF8.GetBytes(
        $"""{{"kind":"AUTHORITY","operationId":"{operationId}","target":{{"kind":"HUMAN","issuer":"https://issuer.example.test/realm","subject":"person"}},"act":{{"kind":"REGISTER"}}}}"""
    )

let private body name =
    if name = "operation.preview" || name = "operation.execute" then
        Array.copy commandInput
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

            Expect.isOk (Codec.decode 100000 Api.session.Output bootstrap) "Bootstrap schema"

            let endpoints =
                Api.all
                |> List.filter (fun e -> (Api.id e) <> "session" && (Api.id e) <> "session.logout")

            for endpoint in endpoints do
                let! status, response = host.Call((Api.id endpoint), body (Api.id endpoint), token)

                Expect.equal status 200 (Api.id endpoint)

                Expect.isOk
                    (StrictJson.read 100000 response |> Result.bind (Api.admitOutput endpoint))
                    "Typed response schema"

                actorTarget (Api.id endpoint) response

            Expect.equal
                (host.HandlerStub.Calls |> List.sort)
                (endpoints |> List.map Api.id |> List.sort)
                "Every core endpoint exactly once"

            let! status, response = host.Call("session.logout", body "session.logout", token)
            Expect.equal status 200 "Logout"

            Expect.isOk (Codec.decode 100000 Api.logout.Output response) "Logout schema"

            let! status, _ = host.Call("context", body "context", token)
            Expect.equal status 401 "Revoked cookie cannot call core"
        })

let private handshakeRefusals () =
    loggedIn true (fun host token ->
        task {
            for fingerprint in [ ""; String.replicate 64 "0" ] do
                let! status, response =
                    host.Call(
                        "operation.execute",
                        body "operation.execute",
                        token,
                        fingerprint = fingerprint
                    )

                Expect.equal status 409 "Contract refusal"
                use payload = parse response

                Expect.equal
                    (payload.RootElement.GetProperty("hostFailure").GetProperty("code").GetString())
                    "CONTRACT_MISMATCH"
                    "Typed host failure"

            let! status, _ =
                host.Send(
                    HttpMethod.Post,
                    "/api/operation.execute",
                    Some(body "operation.execute"),
                    [
                        "Origin", origin.GetLeftPart(UriPartial.Authority)
                        "X-ClaimCore-Antiforgery", token
                    ]
                )

            Expect.equal status 409 "Missing header"
            Expect.isEmpty host.HandlerStub.Calls "No factory/core dispatch on mismatch"
            Expect.isNone host.HandlerStub.Principal "No actor factory on mismatch"
            let! status, _ = host.Call("operation.execute", body "operation.execute", token)
            Expect.equal status 200 "Matching header"
            Expect.equal host.HandlerStub.Calls [ "operation.execute" ] "One matching dispatch"
        })

let private gateRefusals () =
    loggedIn true (fun host token ->
        task {
            let! status, _ =
                host.Call("operation.execute", body "operation.execute", "incorrect")

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
            Expect.isEmpty host.HandlerStub.Calls "Refusing guards never call core"
        })

let private outcomes () =
    loggedIn true (fun host token ->
        task {
            let accepted = receipt operationId "WEB-001"

            let values =
                [
                    ExecuteResult.Accepted accepted, "ACCEPTED"
                    ExecuteResult.AlreadyAccepted accepted, "ALREADY_ACCEPTED"
                    ExecuteResult.Rejected((ClaimCore.Domain.Refusal.CaseVoided)), "REJECTED"
                    ExecuteResult.Failed CoreFault.StoreUnavailable, "FAILED"
                    ExecuteResult.OutcomeUnknown, "OUTCOME_UNKNOWN"
                ]

            for value, tag in values do
                host.HandlerStub.Execute <-
                    (fun (_, ct) ->
                        Expect.equal
                            ct
                            CancellationToken.None
                            "Dispatched mutations are not request-cancellable"

                        Task.FromResult value)

                let! status, response =
                    host.Call("operation.execute", body "operation.execute", token)

                Expect.equal status 200 "Outcomes stay typed"

                Expect.isOk (Codec.decode 100000 Api.operationExecute.Output response) "Schema"

                use payload = parse response

                Expect.equal
                    (payload.RootElement.GetProperty("operationId").GetGuid())
                    operationId
                    "Every outcome, including refusal, echoes admitted request identity"

                Expect.equal
                    (payload.RootElement.GetProperty("outcome").GetProperty("kind").GetString())
                    tag
                    "Outcome identity"
        })

let private authorityCancellation () =
    loggedIn true (fun host token ->
        task {
            host.HandlerStub.ExecuteAuthority <-
                fun (request, ct) ->
                    Expect.equal
                        ct
                        CancellationToken.None
                        "Authority mutations are not request-cancellable"

                    Task.FromResult(ExecuteResult.Accepted(authorityReceipt request))

            let! status, response = host.Call("operation.execute", authorityBody, token)

            Expect.equal status 200 "Authority dispatch"

            Expect.isOk
                (Codec.decode 100000 Api.operationExecute.Output response)
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

                let! status, _ =
                    host.Call("operation.execute", Encoding.UTF8.GetBytes changed, token)

                Expect.equal status 400 "Permanent malformed format refuses before dispatch"

            Expect.isEmpty host.HandlerStub.Calls "Malformed wire never reaches Application"
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
                host.Call("operation.execute", Encoding.UTF8.GetBytes changed, token)

            Expect.equal status 200 "Business format refusal is a typed outcome."

            match Codec.decode 100000 Api.operationExecute.Output response with
            | Ok {
                     Outcome = ExecuteResult.Rejected value
                 } ->
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
        Expect.isEmpty host.HandlerStub.Calls "Health has no actor facade"

let private authorityPreview () =
    task {
        use! fixture = OidcCoreFixture.createFor oidc.Issuer.AbsoluteUri "synthetic-human"
        use host = new Host(true, forActor = fixture.Actor)
        let! _ = host.Login()
        let! token = host.Token()
        let original = fixture.EntryCount
        let! status, response = host.Call("operation.preview", authorityBody, token)
        Expect.equal status 200 "Authority preview uses the unified route"

        match Codec.decode 100000 Api.operationPreview.Output response with
        | Ok {
                 OperationId = id
                 Outcome = PreviewResult.Reviewed(OperationReview.Authority review)
             } ->
            Expect.equal id operationId "Original operation identity"
            Expect.isNone review.Target "Registration has no accepted target yet"
            Expect.isFalse review.Before.Enabled "Registration begins absent"
            Expect.isTrue review.After.Enabled "Registration proposes enabled authority"
        | _ -> failtest "Expected an authority review"

        Expect.equal fixture.EntryCount original "Preview records nothing"
        let! status, response = host.Call("operation.execute", authorityBody, token)
        Expect.equal status 200 "Execute the reviewed input unchanged"

        Expect.isOk
            (Codec.decode 100000 Api.operationExecute.Output response)
            "Shared execution reply"

        Expect.equal fixture.EntryCount (original + 1) "Execution records once"
    }
    |> _.GetAwaiter().GetResult()

let private completionFailure () =
    loggedIn true (fun host token ->
        task {
            let invalid =
                { receipt operationId "WEB-001" with
                    RecordedBy = Guid.Empty
                }

            host.HandlerStub.Execute <- fun _ -> Task.FromResult(ExecuteResult.Accepted invalid)

            let! status, response =
                host.Call("operation.execute", body "operation.execute", token)

            Expect.equal status 500 "Encoding rejects the invalid core receipt"

            Expect.equal
                (HostFailureCodecs.decode response)
                (Ok(HostFailureCodecs.ofReason HostFailureReason.CompletedResponseFailed))
                "A returned core result is completed before response encoding"
        })

let private closedRuntime () =
    loggedIn true (fun host token ->
        task {
            host.HandlerStub.RuntimeAvailable <- false

            for endpoint in [ "context"; "operation.preview"; "operation.execute" ] do
                let! status, response = host.Call(endpoint, body endpoint, token)
                Expect.equal status 200 "Closed runtime returns a typed failure"
                use document = JsonDocument.Parse response
                let outcome = document.RootElement.GetProperty("outcome")

                Expect.equal
                    (outcome.GetProperty("kind").GetString())
                    "FAILED"
                    "Definite refusal to start"

                Expect.equal
                    (outcome.GetProperty("data").GetProperty("code").GetString())
                    "CORE_STORE_UNAVAILABLE"
                    "Preserved public fault"

                if endpoint <> "context" then
                    Expect.equal
                        (document.RootElement.GetProperty("operationId").GetGuid())
                        operationId
                        "Unavailable reply retains the original ID"

            Expect.isNone host.HandlerStub.Principal "Denied work never invokes the actor factory"
            Expect.isEmpty host.HandlerStub.Calls "No core call after runtime leases close"
        })

let private principalAdmission () =
    task {
        use! fixture = OidcCoreFixture.createFor oidc.Issuer.AbsoluteUri "synthetic-human"
        use host = new Host(true, forActor = fixture.Actor)
        let! _ = host.Login()
        let! token = host.Token()
        let originalEntries = fixture.EntryCount

        let target kind issuer subject =
            System.Text.Json.JsonSerializer.SerializeToUtf8Bytes
                {|
                    kind = "AUTHORITY"
                    operationId = operationId
                    target =
                        {|
                            kind = kind
                            issuer = issuer
                            subject = subject
                        |}
                    act = {| kind = "REGISTER" |}
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

            let admitted = Codec.decode 100000 Api.operationExecute.Input input |> Result.isOk

            Expect.equal admitted true "Generic wire syntax does not claim identity admission"

            let! status, response = host.Call("operation.execute", input, token)
            Expect.equal status 200 "Principal content is an Application refusal."

            match Codec.decode 100000 Api.operationExecute.Output response with
            | Ok {
                     Outcome = ExecuteResult.Rejected refusal
                 } ->
                Expect.equal
                    (Error refusal)
                    (PrincipalKey.ofKind
                        (Vocabulary.tryParse PrincipalKind.vocabulary kind |> Option.get)
                        issuer
                        subject)
                    "Application preserves the authoritative identity refusal."
            | _ -> failtest "Invalid principal must receive a typed refusal."

        Expect.equal fixture.EntryCount originalEntries "Refused principal inputs append nothing."
    }
    |> _.GetAwaiter().GetResult()

[<Tests>]
let tests =
    testList
        "HTTP service"
        [
            testCase
                "generic principal syntax reaches Application admission without recording refusals"
                principalAdmission
            testCase
                "authority execution preserves mutation cancellation semantics"
                authorityCancellation
            testCase "raw malformed numeric and unsafe Unicode inputs cannot dispatch" malformedWire
            testCase
                "business scalar formats reject through real Application after HTTP dispatch"
                businessFormatRefusals
            testCase "all registered endpoints use the registry and typed handlers" catalogueRoutes
            testCase "authority preview reaches the shared core without recording" authorityPreview
            testCase "core completion precedes response encoding failure" completionFailure
            testCase
                "closed runtime preserves typed read preview and execute failures"
                closedRuntime
            testCase
                "matching contract dispatches; missing and different contracts never dispatch"
                handshakeRefusals
            testCase
                "antiforgery, shapes, methods and literal paths have refusing controls"
                gateRefusals
            testCase
                "every execute outcome reaches HTTP without cancellation reclassification"
                outcomes
            testCase "live and ready are exact read-only operational probes" readiness
        ]
    |> testSequenced
