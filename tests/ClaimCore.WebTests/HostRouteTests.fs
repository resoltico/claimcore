module ClaimCore.WebTests.HostRouteTests

open System.Net.Http
open Expecto
open ClaimCore.Application
open ClaimCore.Web
open ClaimCore.WebTests.TestServerFixture

let private obsoleteBootstrapRoute () =
    use host = Host.Start()

    let rejected =
        host.Send(
            HttpMethod.Post,
            "/api/v3/session/login",
            Some "{}",
            Some "application/json",
            None
        )

    Expect.equal rejected.Status 404 "Old shared-credential login route is absent"
    Expect.equal host.Runtime.CoreCalls 0 "Old route cannot reach case work"

let private oidcSessionLogout () =
    use host = Host.Start()
    Expect.equal (host.Login()).Status 200 "Synthetic OIDC cookie is issued by the test fixture"
    let token = host.SessionToken()
    let current = host.Send(HttpMethod.Get, "/api/v3/session", None, None, None)
    use currentBody = document current

    Expect.isTrue
        (currentBody.RootElement
            .GetProperty("outcome")
            .GetProperty("data")
            .GetProperty("authenticated")
            .GetBoolean())
        "Browser session derives from the authenticated cookie"

    let logout =
        host.Send(
            HttpMethod.Post,
            "/api/v3/session/logout",
            Some "{}",
            Some "application/json",
            Some token
        )

    Expect.equal logout.Status 200 "CSRF-protected logout succeeds"

let private oidcReturnTarget () =
    Expect.equal
        (HostRoutes.loginReturnProperties ()).RedirectUri
        "/"
        "Successful OIDC callback returns to the application, never its challenge URL"

let private definitionRefusal () =
    use noIssuer = Host.Start(oidcEnabled = false)

    let unavailable =
        noIssuer.Send(HttpMethod.Get, "/api/v3/definition", None, None, None)

    Expect.equal unavailable.Status 401 "An unconfigured issuer cannot expose the definition"

    use host = Host.Start()
    let anonymous = host.Send(HttpMethod.Get, "/api/v3/definition", None, None, None)
    Expect.equal anonymous.Status 401 "An anonymous definition is not disclosed"
    use anonymousBody = document anonymous

    Expect.equal
        (anonymousBody.RootElement.GetProperty("code").GetString())
        "WEB_SESSION_REJECTED"
        "The anonymous refusal uses the closed host diagnostic"

    Expect.equal (host.Login()).Status 200 "Synthetic browser identity is admitted"

    let available = host.Send(HttpMethod.Get, "/api/v3/definition", None, None, None)
    Expect.equal available.Status 200 "An authorized definition is available"

    host.Runtime.DefinitionOutcome <- Some(QueryOutcome.Rejected Rejection.ResourceUnavailable)
    let refused = host.Send(HttpMethod.Get, "/api/v3/definition", None, None, None)
    Expect.equal refused.Status 200 "Actor refusal retains a typed definition outcome"
    use body = document refused

    Expect.equal
        (body.RootElement.GetProperty("outcome").GetProperty("tag").GetString())
        "REJECTED"
        "Core refusal remains an actor rejection, not a session failure"

    Expect.equal
        (body.RootElement.GetProperty("outcome").GetProperty("data").GetProperty("code").GetString())
        "RESOURCE_UNAVAILABLE"
        "Core refusal retains its actual reason"

let tests =
    testList
        "OIDC session route boundaries"
        [
            testCase
                "[CC-WEB-001] shared bootstrap route is physically absent"
                obsoleteBootstrapRoute
            testCase
                "[CC-WEB-001] OIDC cookie session logs out through CSRF admission"
                oidcSessionLogout
            testCase
                "[CC-WEB-001] OIDC challenge returns locally after PKCE callback"
                oidcReturnTarget
            testCase
                "[CC-WEB-001] definition refuses a core denial without disclosure"
                definitionRefusal
        ]
