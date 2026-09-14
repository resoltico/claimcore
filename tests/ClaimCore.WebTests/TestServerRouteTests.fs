module ClaimCore.WebTests.TestServerRouteTests

open System.Net.Http
open Expecto
open ClaimCore.Contracts
open ClaimCore.WebTests.TestServerFixture

let private operation = "40000000-0000-4000-8000-000000000001"

let private draft =
    """{"operationId":"40000000-0000-4000-8000-000000000001","caseReference":"WEB-V2-001","expectedRevision":"0","command":{"kind":"OPEN","values":{"incidentDate":"2026-09-01","incidentNotificationDate":"2026-09-02","incidentCountry":"Latvia","claimantName":"Synthetic claimant","insurerName":"Synthetic insurer","claimedAmount":"12.34","claimedCurrency":"EUR"}}}"""

let private input identifier =
    match identifier with
    | "case.get" -> """{"caseReference":"WEB-V2-001"}"""
    | "case.list" -> """{"limit":10}"""
    | "recovery.list" -> """{"view":"PENDING","limit":10}"""
    | "case.history" -> """{"caseReference":"WEB-V2-001","limit":10,"detail":"FULL"}"""
    | "operation.observe" -> $"""{{"operationId":"{operation}"}}"""
    | "recovery.inspect" -> $"""{{"operationId":"{operation}","attemptLimit":10}}"""
    | "command.prepare" -> draft
    | "command.execute"
    | "recovery.resolve" -> $"""{{"operationId":"{operation}","requestSha256":"{digest}"}}"""
    | "recovery.dismiss" ->
        $"""{{"operationId":"{operation}","requestSha256":"{digest}","confirmed":true}}"""
    | "recovery.export" -> $"""{{"operationId":"{operation}","requestSha256":"{digest}"}}"""
    | _ -> failtest "Every generated JSON endpoint needs a synthetic valid request."

let private snapshotAuthenticated (reply: Reply) expected =
    use body = document reply

    let actual =
        body.RootElement
            .GetProperty("outcome")
            .GetProperty("data")
            .GetProperty("authenticated")
            .GetBoolean()

    Expect.equal actual expected "Session snapshot uses the actual authenticated principal"

let private exerciseEndpoint (host: Host) token (endpoint: WebEndpoint) =
    if
        endpoint.Identifier <> "session.login"
        && endpoint.Identifier <> "session.logout"
    then
        let reply =
            match endpoint.Body with
            | None -> host.Send(HttpMethod.Get, endpoint.Path, None, None, None)
            | Some(JsonBody _) ->
                host.Send(
                    HttpMethod.Post,
                    endpoint.Path,
                    Some(input endpoint.Identifier),
                    Some "application/json",
                    Some token
                )
            | Some(RawBody(mediaType, _, requiredHeaders)) ->
                let headers = requiredHeaders |> List.map (fun (name, _) -> name, digest)

                host.SendWithHeaders(
                    HttpMethod.Post,
                    endpoint.Path,
                    Some "{}",
                    Some mediaType,
                    Some token,
                    headers
                )

        Expect.equal
            reply.Status
            200
            $"Production route {endpoint.Identifier} accepts valid admission"

        use body = document reply

        if endpoint.Identifier = "session" then
            snapshotAuthenticated reply true
        else
            Expect.equal
                (body.RootElement.GetProperty("endpoint").GetString())
                endpoint.Identifier
                "The actual route returns its generated endpoint identity"

let private routeCatalog () =
    use host = Host.Start()
    let projection = ContractProjection.current ()
    Expect.equal projection.WebEndpoints.Length 19 "Exact generated HTTP-v2 route count"
    let login = host.Login()
    Expect.equal login.Status 200 "Synthetic TestServer login succeeds"
    let token = host.SessionToken()
    projection.WebEndpoints |> List.iter (exerciseEndpoint host token)

    let logout =
        host.Send(
            HttpMethod.Post,
            "/api/v2/session/logout",
            Some "{}",
            Some "application/json",
            Some token
        )

    Expect.equal logout.Status 200 "The nineteenth production endpoint admits logout"

    use logoutBody = document logout

    Expect.equal
        (logoutBody.RootElement.GetProperty("endpoint").GetString())
        "session.logout"
        "Logout retains the generated endpoint identity"

    Expect.isGreaterThan
        host.Runtime.CoreCalls
        0
        "Core query/prepare routes reached the typed facade"

    Expect.isGreaterThan
        host.Runtime.RecoveryCalls
        0
        "Recovery routes reached the typed recovery facade"

let private unknownRoutes () =
    use host = Host.Start()

    for path in [ "/api/v1/query"; "/api/v2/not-a-route"; "/api/extra" ] do
        let reply = host.Send(HttpMethod.Get, path, None, None, None)
        Expect.equal reply.Status 404 "Retired and unknown API paths do not use the SPA fallback"
        Expect.stringContains reply.CacheControl "no-store" "API absence is private and uncached"
        use body = document reply

        Expect.equal
            (body.RootElement.GetProperty("kind").GetString())
            "HOST_FAILURE"
            "Typed host failure"

        Expect.equal
            (body.RootElement.GetProperty("code").GetString())
            "WEB_NOT_FOUND"
            "Exact not-found code"

    let spa = host.Send(HttpMethod.Get, "/not-an-api-path", None, None, None)
    Expect.equal spa.Status 200 "Only non-API paths may use the SPA fallback"

let private sessionLifecycle () =
    use host = Host.Start()
    let anonymous = host.Send(HttpMethod.Get, "/api/v2/session", None, None, None)
    snapshotAuthenticated anonymous false
    let login = host.Login()
    Expect.equal login.Status 200 "Real cookie and antiforgery login succeeds"
    snapshotAuthenticated login true
    let definition = host.Send(HttpMethod.Get, "/api/v2/definition", None, None, None)
    Expect.equal definition.Status 200 "Authenticated definition is reachable"
    let token = host.SessionToken()

    let logout =
        host.Send(
            HttpMethod.Post,
            "/api/v2/session/logout",
            Some "{}",
            Some "application/json",
            Some token
        )

    Expect.equal logout.Status 200 "Real logout succeeds"
    snapshotAuthenticated logout false
    let rejected = host.Send(HttpMethod.Get, "/api/v2/definition", None, None, None)
    Expect.equal rejected.Status 401 "Revoked session cannot read a definition"
    let finalSnapshot = host.Send(HttpMethod.Get, "/api/v2/session", None, None, None)
    snapshotAuthenticated finalSnapshot false

let tests =
    testList
        "Web HTTP-v2 TestServer"
        [
            testCase
                "[CC-WEB-001] production route map dispatches all nineteen v2 endpoints"
                routeCatalog
            testCase
                "[CC-WEB-001] retired and unknown API routes return typed no-store 404"
                unknownRoutes
            testCase
                "[CC-WEB-001] session login and logout revoke admission through real cookies and antiforgery"
                sessionLifecycle
        ]
