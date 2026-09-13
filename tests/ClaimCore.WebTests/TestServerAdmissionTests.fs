module ClaimCore.WebTests.TestServerAdmissionTests

open System.Net.Http
open Expecto
open ClaimCore.Contracts
open ClaimCore.WebTests.TestServerFixture

let private authenticatedRoutes () =
    (ContractProjection.current ()).WebEndpoints
    |> List.filter (fun endpoint ->
        endpoint.Identifier <> "session"
        && endpoint.Identifier <> "session.login"
        && endpoint.Identifier <> "session.logout"
        && endpoint.Identifier <> "definition")

let private media (endpoint: WebEndpoint) =
    match endpoint.Body with
    | Some(JsonBody _) -> "application/json"
    | Some(RawBody(value, _, _)) -> value
    | None -> failtest "Authenticated mutation routes must declare a body."

let private failure (reply: Reply) status code =
    Expect.equal reply.Status status "Production HTTP status"
    Expect.stringContains reply.CacheControl "no-store" "Sensitive failures are not cached"
    use body = document reply
    Expect.equal (body.RootElement.GetProperty("kind").GetString()) "HOST_FAILURE" "Exact host body"
    Expect.equal (body.RootElement.GetProperty("code").GetString()) code "Exact host code"

let private allRouteAdmission () =
    let routes = authenticatedRoutes ()
    Expect.equal routes.Length 15 "All generated authenticated application routes are covered"
    use anonymous = Host.Start()
    use authenticated = Host.Start()
    let login = authenticated.Login()
    Expect.equal login.Status 200 "Synthetic login admits the authenticated test client"

    for endpoint in routes do
        let withoutSession =
            anonymous.Send(HttpMethod.Post, endpoint.Path, Some "{}", Some(media endpoint), None)

        failure withoutSession 401 "WEB_SESSION_REJECTED"

        let wrongOrigin =
            authenticated.SendWithHeaders(
                HttpMethod.Post,
                endpoint.Path,
                Some "{}",
                Some(media endpoint),
                None,
                [ "Origin", "https://not-local.example" ]
            )

        failure wrongOrigin 403 "WEB_ORIGIN_REJECTED"

        let missingAntiforgery =
            authenticated.Send(
                HttpMethod.Post,
                endpoint.Path,
                Some "{}",
                Some(media endpoint),
                None
            )

        failure missingAntiforgery 403 "WEB_CSRF_REJECTED"

    Expect.equal anonymous.Runtime.CoreCalls 0 "Anonymous requests never reach core"
    Expect.equal anonymous.Runtime.RecoveryCalls 0 "Anonymous requests never reach recovery"

    Expect.equal
        authenticated.Runtime.CoreCalls
        0
        "Rejected authenticated requests never reach core"

    Expect.equal
        authenticated.Runtime.RecoveryCalls
        0
        "Rejected authenticated requests never reach recovery"

let private rawAdmission () =
    use host = Host.Start()
    Expect.equal (host.Login()).Status 200 "Synthetic login"
    let token = host.SessionToken()

    let routes =
        authenticatedRoutes ()
        |> List.choose (fun endpoint ->
            match endpoint.Body with
            | Some(RawBody(mediaType, maximumBytes, headers)) ->
                Some(endpoint, mediaType, maximumBytes, headers)
            | _ -> None)

    Expect.equal routes.Length 4 "Envelope and canonical-record preview/retain are separate routes"

    for endpoint, mediaType, maximumBytes, headers in routes do
        let wrongMedia =
            host.Send(
                HttpMethod.Post,
                endpoint.Path,
                Some "{}",
                Some "application/json",
                Some token
            )

        failure wrongMedia 415 "WEB_MEDIA_TYPE"

        let parameterized =
            host.Send(
                HttpMethod.Post,
                endpoint.Path,
                Some "{}",
                Some(mediaType + "; charset=utf-8"),
                Some token
            )

        failure parameterized 415 "WEB_MEDIA_TYPE"

        let overLimit =
            host.Send(
                HttpMethod.Post,
                endpoint.Path,
                Some(String.replicate (maximumBytes + 1) "x"),
                Some mediaType,
                Some token
            )

        failure overLimit 413 "WEB_BODY_TOO_LARGE"

        if not headers.IsEmpty then
            let missingDigest =
                host.Send(HttpMethod.Post, endpoint.Path, Some "{}", Some mediaType, Some token)

            failure missingDigest 400 "WEB_INVALID_REQUEST"

    Expect.equal host.Runtime.RecoveryCalls 0 "Malformed raw import requests never reach retention"

let private malformedExportStatus () =
    use malformedExport = Host.Start(invalidExportMetadata = true)
    Expect.equal (malformedExport.Login()).Status 200 "Synthetic metadata host login"
    let exportToken = malformedExport.SessionToken()

    let invalidAttachment =
        malformedExport.Send(
            HttpMethod.Post,
            "/api/v2/recovery/export",
            Some(
                $"""{{"operationId":"40000000-0000-4000-8000-000000000001","requestSha256":"{digest}"}}"""
            ),
            Some "application/json",
            Some exportToken
        )

    failure invalidAttachment 500 "WEB_PROTOCOL"

let private observedFailureStatuses () =
    use host = Host.Start(loginPermits = 1)
    let unknown = host.Send(HttpMethod.Get, "/api/v2/absent", None, None, None)
    failure unknown 404 "WEB_NOT_FOUND"

    let anonymous =
        host.Send(HttpMethod.Post, "/api/v2/cases/list", Some "{}", Some "application/json", None)

    failure anonymous 401 "WEB_SESSION_REJECTED"
    let login = host.Login()
    Expect.equal login.Status 200 "First login is inside the limiter"
    let second = host.Login()
    failure second 429 "WEB_BUSY"
    let token = host.SessionToken()

    let malformed =
        host.Send(
            HttpMethod.Post,
            "/api/v2/cases/list",
            Some "{",
            Some "application/json",
            Some token
        )

    failure malformed 400 "WEB_INVALID_REQUEST"
    use malformedBody = document malformed

    Expect.equal
        (malformedBody.RootElement.GetProperty("executionPhase").GetString())
        "NOT_STARTED"
        "Malformed mutation input proves no core invocation"

    let wrongMedia =
        host.Send(HttpMethod.Post, "/api/v2/cases/list", Some "{}", Some "text/plain", Some token)

    failure wrongMedia 415 "WEB_MEDIA_TYPE"

    let oversized =
        host.Send(
            HttpMethod.Post,
            "/api/v2/cases/list",
            Some(String.replicate 65537 "x"),
            Some "application/json",
            Some token
        )

    failure oversized 413 "WEB_BODY_TOO_LARGE"

    let noCsrf =
        host.Send(HttpMethod.Post, "/api/v2/cases/list", Some "{}", Some "application/json", None)

    failure noCsrf 403 "WEB_CSRF_REJECTED"
    malformedExportStatus ()

let tests =
    testList
        "Web HTTP-v2 TestServer"
        [
            testCase
                "[CC-WEB-001] every authenticated route rejects absent session, origin, and antiforgery before core dispatch"
                allRouteAdmission
            testCase
                "[CC-WEB-001] raw import routes enforce exact media, body bounds, and source digest"
                rawAdmission
            testCase
                "[CC-WEB-001] HTTP failure statuses and execution phases use exact generated host bodies"
                observedFailureStatuses
        ]
