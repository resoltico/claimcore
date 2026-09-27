module ClaimCore.WebTests.TestServerRouteTests

open System.Net.Http
open Expecto
open ClaimCore.Contracts
open ClaimCore.WebTests.TestServerFixture
open ClaimCore.WebTests.TestServerRouteInputs

let private snapshotAuthenticated (reply: Reply) expected =
    use body = document reply

    let actual =
        body.RootElement
            .GetProperty("outcome")
            .GetProperty("data")
            .GetProperty("authenticated")
            .GetBoolean()

    Expect.equal actual expected "Session snapshot uses the actual authenticated principal"

let private requireTypedRouteOutcome (endpoint: WebEndpoint) (body: System.Text.Json.JsonDocument) =
    let outcome = body.RootElement.GetProperty("outcome")

    if endpoint.Identifier.StartsWith("lifecycle.", System.StringComparison.Ordinal) then
        Expect.equal
            (outcome.GetProperty("tag").GetString())
            "RESOURCE_UNAVAILABLE"
            "The route reaches the typed lifecycle workflow stub"

    if endpoint.Identifier.StartsWith("authority.", System.StringComparison.Ordinal) then
        if
            endpoint.Identifier = "authority.approveCopySigner"
            || endpoint.Identifier = "authority.approveCopyDeletion"
            || endpoint.Identifier = "authority.approveCopyAdoption"
            || endpoint.Identifier = "authority.approveWriterHandoff"
            || endpoint.Identifier = "authority.approveRealDataActivation"
        then
            Expect.equal
                (outcome.GetProperty("tag").GetString())
                "STARTED_UNCONFIRMED"
                "Actor approval preserves the typed uncertain result"
        else
            Expect.equal
                (outcome.GetProperty("tag").GetString())
                "RESOURCE_UNAVAILABLE"
                "Inaccessible authority remains non-disclosing"

            Expect.equal
                (outcome.GetProperty("data").ValueKind)
                System.Text.Json.JsonValueKind.Null
                "The refusal contains no actor or event payload"

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

            requireTypedRouteOutcome endpoint body

let private managementAndLifecycleRoutes =
    ContractProjection.current().WebEndpoints
    |> List.filter (fun endpoint ->
        endpoint.Identifier.StartsWith("authority.", System.StringComparison.Ordinal)
        || endpoint.Identifier.StartsWith("lifecycle.", System.StringComparison.Ordinal))
    |> List.map (fun endpoint ->
        testCase
            $"[CC-WEB-001] endpoint {endpoint.Identifier} dispatches authenticated route"
            (fun () ->
                use unauthorized = Host.Start()

                let denied =
                    unauthorized.Send(
                        HttpMethod.Post,
                        endpoint.Path,
                        Some "{}",
                        Some "application/json",
                        None
                    )

                Expect.equal denied.Status 401 "A caller without a session cannot reach the route"
                Expect.stringContains denied.CacheControl "no-store" "Denial is not cached"
                use denial = document denied

                Expect.equal
                    (denial.RootElement.GetProperty("code").GetString())
                    "WEB_SESSION_REJECTED"
                    "The refusal is typed and does not identify a resource"

                Expect.equal
                    unauthorized.Runtime.ManagementCalls
                    0
                    "Denied calls never reach management"

                Expect.equal unauthorized.Runtime.CoreCalls 0 "Denied calls never reach core"

                Expect.equal
                    unauthorized.Runtime.RecoveryCalls
                    0
                    "Denied calls never reach recovery"

                use host = Host.Start()
                Expect.equal (host.Login().Status) 200 "Synthetic session starts"
                exerciseEndpoint host (host.SessionToken()) endpoint

                if
                    endpoint.Identifier.StartsWith("authority.", System.StringComparison.Ordinal)
                    && endpoint.Identifier <> "authority.approveCopySigner"
                    && endpoint.Identifier <> "authority.approveCopyDeletion"
                    && endpoint.Identifier <> "authority.approveCopyAdoption"
                    && endpoint.Identifier <> "authority.approveWriterHandoff"
                    && endpoint.Identifier <> "authority.approveRealDataActivation"
                    && endpoint.Identifier <> "authority.reviewRealDataActivation"
                then
                    Expect.equal
                        host.Runtime.ManagementCalls
                        1
                        "Only this authority endpoint reached the management facade"))

let private routeCatalog () =
    use host = Host.Start()
    let projection = ContractProjection.current ()
    Expect.equal projection.WebEndpoints.Length 33 "Exact generated HTTP-v3 route count"
    let login = host.Login()
    Expect.equal login.Status 200 "Synthetic TestServer login succeeds"
    let token = host.SessionToken()
    projection.WebEndpoints |> List.iter (exerciseEndpoint host token)

    let logout =
        host.Send(
            HttpMethod.Post,
            "/api/v3/session/logout",
            Some "{}",
            Some "application/json",
            Some token
        )

    Expect.equal logout.Status 200 "The session logout endpoint admits the request"

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

    Expect.isGreaterThan
        host.Runtime.ManagementCalls
        0
        "Authority routes reached only the authenticated management facade"

let private unknownRoutes () =
    use host = Host.Start()

    for path in [ "/api/v1/query"; "/api/v2/cases/get"; "/api/v3/not-a-route"; "/api/extra" ] do
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
    let anonymous = host.Send(HttpMethod.Get, "/api/v3/session", None, None, None)
    snapshotAuthenticated anonymous false
    let login = host.Login()
    Expect.equal login.Status 200 "Real cookie and antiforgery login succeeds"
    let authenticated = host.Send(HttpMethod.Get, "/api/v3/session", None, None, None)
    snapshotAuthenticated authenticated true
    let definition = host.Send(HttpMethod.Get, "/api/v3/definition", None, None, None)
    Expect.equal definition.Status 200 "Authenticated definition is reachable"
    let token = host.SessionToken()

    let logout =
        host.Send(
            HttpMethod.Post,
            "/api/v3/session/logout",
            Some "{}",
            Some "application/json",
            Some token
        )

    Expect.equal logout.Status 200 "Real logout succeeds"
    snapshotAuthenticated logout false
    let rejected = host.Send(HttpMethod.Get, "/api/v3/definition", None, None, None)
    Expect.equal rejected.Status 401 "Revoked session cannot read a definition"
    let finalSnapshot = host.Send(HttpMethod.Get, "/api/v3/session", None, None, None)
    snapshotAuthenticated finalSnapshot false

let tests =
    testList
        "Web HTTP-v3 TestServer"
        [
            testCase
                "[CC-WEB-001] production route map dispatches the exact v3 endpoints"
                routeCatalog
            testCase
                "[CC-WEB-001] retired and unknown API routes return typed no-store 404"
                unknownRoutes
            testCase
                "[CC-WEB-001] session login and logout revoke admission through real cookies and antiforgery"
                sessionLifecycle
            yield! managementAndLifecycleRoutes
        ]
