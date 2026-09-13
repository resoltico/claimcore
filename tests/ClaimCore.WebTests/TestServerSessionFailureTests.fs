module ClaimCore.WebTests.TestServerSessionFailureTests

open System.Net.Http
open Expecto
open ClaimCore.WebTests.TestServerFixture

let private failure (reply: Reply) status code =
    Expect.equal reply.Status status "Exact session refusal status"
    Expect.stringContains reply.CacheControl "no-store" "Session refusals are not cached"
    use body = document reply

    Expect.equal
        (body.RootElement.GetProperty("kind").GetString())
        "HOST_FAILURE"
        "Host failure shape"

    Expect.equal (body.RootElement.GetProperty("code").GetString()) code "Exact refusal code"

let private post (host: Host) path body token =
    host.Send(HttpMethod.Post, path, Some body, Some "application/json", token)

let private loginBodyRefusals () =
    use host = Host.Start()
    let token = host.SessionToken()
    let path = "/api/v2/session/login"
    failure (post host path "{" (Some token)) 400 "WEB_INVALID_REQUEST"
    failure (post host path "[]" (Some token)) 400 "WEB_INVALID_REQUEST"

    failure (post host path "{\"credential\":\"synthetic\"}" (Some token)) 400 "WEB_INVALID_REQUEST"

    failure (post host path (String.replicate 4097 "x") (Some token)) 413 "WEB_BODY_TOO_LARGE"

    Expect.equal host.Runtime.CoreCalls 0 "Malformed login does not enter claims work"

let private loginAuthenticationRefusals () =
    use host = Host.Start()
    let token = host.SessionToken()
    let path = "/api/v2/session/login"

    let body credential antiforgery =
        $"""{{"credential":"{credential}","antiforgeryToken":"{antiforgery}"}}"""

    failure (post host path (body "synthetic-wrong" token) (Some token)) 401 "WEB_LOGIN_REJECTED"

    failure
        (post host path (body credential "synthetic-wrong") (Some token))
        401
        "WEB_LOGIN_REJECTED"

    failure (post host path (body credential token) None) 403 "WEB_CSRF_REJECTED"

    use snapshot =
        host.Send(HttpMethod.Get, "/api/v2/session", None, None, None) |> document

    Expect.isFalse
        (snapshot.RootElement
            .GetProperty("outcome")
            .GetProperty("data")
            .GetProperty("authenticated")
            .GetBoolean())
        "Rejected login creates no browser session"

let private logoutRefusals () =
    use host = Host.Start()
    let path = "/api/v2/session/logout"
    failure (post host path "{}" None) 401 "WEB_SESSION_REJECTED"
    Expect.equal (host.Login()).Status 200 "A real synthetic login succeeds"
    let token = host.SessionToken()
    failure (post host path "{}" None) 403 "WEB_CSRF_REJECTED"
    failure (post host path "{" (Some token)) 400 "WEB_INVALID_REQUEST"
    failure (post host path "{\"unexpected\":true}" (Some token)) 400 "WEB_INVALID_REQUEST"

    failure (post host path (String.replicate 1025 "x") (Some token)) 413 "WEB_BODY_TOO_LARGE"

    let stillCurrent = host.Send(HttpMethod.Get, "/api/v2/definition", None, None, None)
    Expect.equal stillCurrent.Status 200 "A rejected logout cannot revoke the session"

let tests =
    testList
        "Web session refusals through TestServer"
        [
            testCase
                "[CC-WEB-001] login refuses malformed and over-limit bodies before admission"
                loginBodyRefusals
            testCase
                "[CC-WEB-001] login rejects invalid bootstrap and antiforgery credentials"
                loginAuthenticationRefusals
            testCase
                "[CC-WEB-001] logout rejects absent session and malformed or unverified bodies"
                logoutRefusals
        ]
