module ClaimCore.WebTests.SecurityTests

open System
open System.IO
open System.Net
open System.Security.Claims
open Expecto
open Microsoft.AspNetCore.Authentication
open Microsoft.AspNetCore.Authentication.Cookies
open Microsoft.AspNetCore.Http
open ClaimCore.Web
open ClaimCore.WebTests.RouteFixtures
open ClaimCore.WebTests.PrivateTestPaths

let private origin = Uri("https://localhost:5443")

let private postContext () =
    let context = DefaultHttpContext()
    context.Connection.RemoteIpAddress <- IPAddress.Loopback
    context.Request.Headers["Host"] <- origin.Authority
    context.Request.Headers["Origin"] <- origin.GetLeftPart(UriPartial.Authority)
    context.Request.ContentType <- "application/json; charset=utf-8"
    context

let private headerTests () =
    let context = DefaultHttpContext()
    HttpHeaders.apply context
    HttpHeaders.noStore context

    Expect.equal
        (context.Response.Headers["Content-Security-Policy"].ToString())
        "default-src 'none'; script-src 'self'; style-src 'self'; style-src-attr 'none'; img-src 'self'; font-src 'self'; connect-src 'self'; object-src 'none'; base-uri 'none'; frame-ancestors 'none'; frame-src 'none'; form-action 'self'; manifest-src 'self'; media-src 'none'; worker-src 'none'"
        "Only same-origin static application content is permitted"

    Expect.equal
        (context.Response.Headers.CacheControl.ToString())
        "no-store, max-age=0"
        "Dynamic claimant-bearing responses are never cached"

let private connectionTests () =
    let publicPeer = postContext ()
    publicPeer.Connection.RemoteIpAddress <- IPAddress.Parse("203.0.113.10")

    Expect.equal
        (Admission.postShape origin RequestBody.Json 32 publicPeer)
        (Error AdmissionFailure.UntrustedConnection)
        "Public peers are rejected"

    let oversized = postContext ()
    oversized.Request.ContentLength <- Nullable 33L

    Expect.equal
        (Admission.postShape origin RequestBody.Json 32 oversized)
        (Error AdmissionFailure.BodyTooLarge)
        "Declared body limits are enforced before allocation"

let private sessionTests () =
    let mutable now = DateTimeOffset(2026, 9, 23, 12, 0, 0, TimeSpan.Zero)

    let store =
        OidcTicketStore(TimeSpan.FromMinutes(1.), TimeSpan.FromMinutes(2.), clock = (fun () -> now))
        :> ITicketStore

    let identity = ClaimsIdentity([ Claim("sub", "synthetic-owner") ], "oidc")
    let properties = AuthenticationProperties()
    properties.ExpiresUtc <- Nullable(now.AddMinutes(10.))

    let ticket =
        AuthenticationTicket(
            ClaimsPrincipal(identity),
            properties,
            CookieAuthenticationDefaults.AuthenticationScheme
        )

    let key = store.StoreAsync(ticket).Result
    now <- now.AddSeconds(59.)
    Expect.isNotNull (store.RetrieveAsync(key).Result) "Activity extends idle within absolute life"
    now <- now.AddSeconds(61.)
    Expect.isNull (store.RetrieveAsync(key).Result) "Absolute expiry revokes the server ticket"

let private stateLeaseTests () =
    let directory = newPrivateDirectory "claimcore-web-tests-"

    try
        if OperatingSystem.IsWindows() then
            Expect.throws
                (fun () -> Security.acquireStateDirectory directory |> ignore)
                "Windows lock fails closed"
        else
            use state = Security.acquireStateDirectory directory

            Expect.throws
                (fun () -> Security.acquireStateDirectory directory |> ignore)
                "A second host cannot share the private state lease"

            Expect.isFalse
                (Directory.GetFiles(directory)
                 |> Array.exists (fun path ->
                     Path.GetFileName(path)
                     |> Option.ofObj
                     |> Option.exists (fun name -> name.StartsWith("bootstrap-credential-"))))
                "OIDC startup creates no shared bootstrap credential"
    finally
        Directory.Delete(directory, true)

let tests =
    testList
        "Web security boundaries"
        [
            testCase "[CC-WEB-001] emits privacy headers and no-store" headerTests
            testCase
                "[CC-WEB-001] limits requests to exact loopback browser admission"
                connectionTests
            testCase "[CC-WEB-001] OIDC ticket enforces idle and absolute expiry" sessionTests
            testCase
                "[CC-WEB-001] private host lease creates no bootstrap credential"
                stateLeaseTests
        ]
