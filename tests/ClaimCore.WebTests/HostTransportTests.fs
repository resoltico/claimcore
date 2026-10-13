module ClaimCore.WebTests.HostTransportTests

open System
open System.Net.Http
open System.Text
open System.Text.Json
open System.Threading
open System.Threading.Tasks
open Expecto
open ClaimCore.Application
open ClaimCore.Contracts
open ClaimCore.Web
open ClaimCore.WebTests.FacadeStub
open ClaimCore.WebTests.TestServerFixture

let private body name =
    EndpointCatalog.tryFind name
    |> Option.get
    |> _.Request
    |> SchemaSamples.document

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

let private catalogueRoutes () =
    loggedIn true (fun host token ->
        task {
            let! status, bootstrap = host.Bootstrap()
            Expect.equal status 200 "Bootstrap GET"
            Expect.isOk (EndpointCatalog.admitResponse "session" bootstrap) "Bootstrap schema"

            for endpoint in
                EndpointCatalog.all
                |> List.filter (fun e ->
                    e.Identifier <> "session" && e.Identifier <> "session.logout") do
                let! status, response =
                    host.Call(endpoint.Identifier, body endpoint.Identifier, token)

                Expect.equal status 200 endpoint.Identifier

                Expect.isOk
                    (EndpointCatalog.admitResponse endpoint.Identifier response)
                    "Typed response schema"

            Expect.equal host.Facade.Calls.Length 12 "Every core endpoint exactly once"
            let! status, response = host.Call("session.logout", body "session.logout", token)
            Expect.equal status 200 "Logout"
            Expect.isOk (EndpointCatalog.admitResponse "session.logout" response) "Logout schema"
            let! status, _ = host.Call("definition", body "definition", token)
            Expect.equal status 401 "Revoked cookie cannot call core"
        })

let private handshakeRefusals () =
    loggedIn true (fun host token ->
        task {
            for fingerprint in [ ""; String.replicate 64 "0" ] do
                let! status, response =
                    host.Call(
                        "command.execute",
                        body "command.execute",
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
                    "/api/command.execute",
                    Some(body "command.execute"),
                    [
                        "Origin", origin.GetLeftPart(UriPartial.Authority)
                        "X-ClaimCore-Antiforgery", token
                    ]
                )

            Expect.equal status 409 "Missing header"
            Expect.isEmpty host.Facade.Calls "No factory/core dispatch on mismatch"
            Expect.isNone host.Facade.Principal "No actor factory on mismatch"
            let! status, _ = host.Call("command.execute", body "command.execute", token)
            Expect.equal status 200 "Matching header"
            Expect.equal host.Facade.Calls [ "command.execute" ] "One matching dispatch"
        })

let private admissionRefusals () =
    loggedIn true (fun host token ->
        task {
            let! status, _ = host.Call("command.execute", body "command.execute", "incorrect")
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

            let! status, _ =
                host.Send(HttpMethod.Post, "/API/definition", Some(body "definition"), [])

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
                    ExecuteOutcome.Rejected Rejection.CaseVoided, "REJECTED"
                    ExecuteOutcome.Failed CoreFault.StoreUnavailable, "FAILED"
                    ExecuteOutcome.Cancelled, "CANCELLED"
                    ExecuteOutcome.OutcomeUnknown(operationId, String.replicate 64 "a"),
                    "OUTCOME_UNKNOWN"
                ]

            for value, tag in values do
                host.Facade.Execute <-
                    (fun (_, ct) ->
                        Expect.equal
                            ct
                            CancellationToken.None
                            "Dispatched mutations are not request-cancellable"

                        Task.FromResult value)

                let! status, response = host.Call("command.execute", body "command.execute", token)
                Expect.equal status 200 "Outcomes stay typed"
                Expect.isOk (EndpointCatalog.admitResponse "command.execute" response) "Schema"
                use payload = parse response

                Expect.equal
                    (payload.RootElement.GetProperty("outcome").GetProperty("tag").GetString())
                    tag
                    "Outcome identity"
        })

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

[<Tests>]
let tests =
    testList
        "HTTP service"
        [
            testCase "all fourteen endpoints use the one catalogue and typed facade" catalogueRoutes
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
