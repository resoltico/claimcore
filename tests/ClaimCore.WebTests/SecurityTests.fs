module ClaimCore.WebTests.SecurityTests

open System
open System.IO
open System.Net
open System.Security.Claims
open System.Text
open Expecto
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
    let sessions = SessionRegistry(TimeSpan.FromMinutes(1.), TimeSpan.FromMinutes(2.))
    let id = sessions.Create(DateTimeOffset.UtcNow)
    let context = DefaultHttpContext()
    context.User <- ClaimsPrincipal(ClaimsIdentity([ Claim("claimcore-session", id) ]))

    Expect.isTrue
        (Admission.isCurrentSession sessions context)
        "Registry-backed principal is current"

    sessions.RevokeAll()

    Expect.isFalse
        (Admission.isCurrentSession sessions context)
        "Global shutdown invalidates sessions"

let private bootstrapCredentialTests () =
    let directory = newPrivateDirectory "claimcore-web-tests-"

    try
        if OperatingSystem.IsWindows() then
            Expect.throws
                (fun () -> Security.acquireStateDirectory directory |> ignore)
                "Windows lock fails closed"
        else
            use state = Security.acquireStateDirectory directory
            use leased = Security.rotateBootstrapCredential directory
            let credential = leased.Credential
            let secret = File.ReadAllText(credential.Path).Trim()

            Expect.isTrue
                (Security.isBootstrapCredential credential secret)
                "The private file verifies"

            Expect.isFalse
                (Security.isBootstrapCredential credential "wrong")
                "A wrong secret is refused"
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
            testCase
                "[CC-WEB-001] makes session validity a server-side registry concern"
                sessionTests
            testCase
                "[CC-WEB-001] maintains a private rotating bootstrap credential"
                bootstrapCredentialTests
        ]
