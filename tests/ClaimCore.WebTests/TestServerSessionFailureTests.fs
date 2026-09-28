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

let private retiredBootstrapRefusals () =
    use host = Host.Start()
    let path = "/api/v3/session/login"
    failure (post host path "{}" None) 404 "WEB_NOT_FOUND"
    failure (post host path "{" None) 404 "WEB_NOT_FOUND"

    Expect.equal host.Runtime.CoreCalls 0 "Retired login cannot enter claims work"

let private mixedCredentialRefusals () =
    use host = Host.Start()
    Expect.equal (host.Login()).Status 200 "Synthetic OIDC cookie is issued"
    let token = host.SessionToken()

    let mixed =
        host.SendWithHeaders(
            HttpMethod.Post,
            "/api/v3/cases/get",
            Some "{}",
            Some "application/json",
            Some token,
            [ "Authorization", "Bearer synthetic-invalid" ]
        )

    failure mixed 401 "WEB_SESSION_REJECTED"
    Expect.equal host.Runtime.CoreCalls 0 "Mixed credentials never enter claims work"

let private logoutRefusals () =
    use host = Host.Start()
    let path = "/api/v3/session/logout"
    failure (post host path "{}" None) 401 "WEB_SESSION_REJECTED"
    Expect.equal (host.Login()).Status 200 "A real synthetic login succeeds"
    let token = host.SessionToken()
    failure (post host path "{}" None) 403 "WEB_CSRF_REJECTED"
    failure (post host path "{" (Some token)) 400 "WEB_INVALID_REQUEST"
    failure (post host path "{\"unexpected\":true}" (Some token)) 400 "WEB_INVALID_REQUEST"

    failure (post host path (String.replicate 1025 "x") (Some token)) 413 "WEB_BODY_TOO_LARGE"

    let stillCurrent = host.Send(HttpMethod.Get, "/api/v3/definition", None, None, None)
    Expect.equal stillCurrent.Status 200 "A rejected logout cannot revoke the session"

let tests =
    testList
        "Web session refusals through TestServer"
        [
            testCase
                "[CC-WEB-001] retired bootstrap route refuses all request bodies"
                retiredBootstrapRefusals
            testCase
                "[CC-WEB-001] mixed bearer and browser credentials are refused"
                mixedCredentialRefusals
            testCase
                "[CC-WEB-001] logout rejects absent session and malformed or unverified bodies"
                logoutRefusals
        ]
