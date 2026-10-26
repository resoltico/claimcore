module ClaimCore.WebTests.RequestGateEdgeTests

open ClaimCore.Contract
open ClaimCore.Domain

open System
open System.Net
open System.Threading.Tasks
open Expecto
open Microsoft.AspNetCore.Antiforgery
open Microsoft.AspNetCore.Http
open Microsoft.Extensions.Primitives
open ClaimCore.Web

let private origin = Uri("https://localhost:5443")

let private context () =
    let value = DefaultHttpContext()
    value.Connection.RemoteIpAddress <- IPAddress.Loopback
    value.Request.Headers.Host <- origin.Authority
    value.Request.Headers.Origin <- origin.GetLeftPart(UriPartial.Authority)
    value.Request.ContentType <- "application/json"
    value

let private jsonShapeTests () =
    let accepted = context ()
    accepted.Request.Headers["Sec-Fetch-Site"] <- "same-origin"

    Expect.equal
        (RequestGate.postShape (WebBindings.create origin "127.0.0.1" origin.Port) 32 accepted)
        (Ok())
        "JSON admission accepts the exact local shape"

    let duplicateHost = context ()

    duplicateHost.Request.Headers["Host"] <- StringValues([| origin.Authority; origin.Authority |])

    Expect.equal
        (RequestGate.postShape (WebBindings.create origin "127.0.0.1" origin.Port) 32 duplicateHost)
        (Error GateFailure.UntrustedConnection)
        "Host header cardinality is exact"

    let wrongMedia = context ()
    wrongMedia.Request.ContentType <- "application/jsonx"

    Expect.equal
        (RequestGate.postShape (WebBindings.create origin "127.0.0.1" origin.Port) 32 wrongMedia)
        (Error GateFailure.UnsupportedMediaType)
        "JSON media is not prefix matched"

let private connectionRefusals () =
    let absentPeer = context ()
    absentPeer.Connection.RemoteIpAddress <- null

    Expect.equal
        (RequestGate.postShape (WebBindings.create origin "127.0.0.1" origin.Port) 32 absentPeer)
        (Error GateFailure.UntrustedConnection)
        "An absent remote address is never assumed local"

    let remotePeer = context ()
    remotePeer.Connection.RemoteIpAddress <- IPAddress.Parse("192.0.2.1")

    Expect.equal
        (RequestGate.postShape (WebBindings.create origin "127.0.0.1" origin.Port) 32 remotePeer)
        (Error GateFailure.UntrustedConnection)
        "A non-loopback peer cannot submit work"

    let wrongHost = context ()
    wrongHost.Request.Headers.Host <- "attacker.example"

    Expect.equal
        (RequestGate.postShape (WebBindings.create origin "127.0.0.1" origin.Port) 32 wrongHost)
        (Error GateFailure.UntrustedConnection)
        "A loopback peer cannot redirect the host authority"

let private browserOriginRefusals () =
    let missingOrigin = context ()
    missingOrigin.Request.Headers.Remove("Origin") |> ignore

    Expect.equal
        (RequestGate.postShape (WebBindings.create origin "127.0.0.1" origin.Port) 32 missingOrigin)
        (Error GateFailure.OriginRejected)
        "A browser mutation requires an explicit origin"

    let duplicateOrigin = context ()

    duplicateOrigin.Request.Headers["Origin"] <-
        StringValues(
            [|
                origin.GetLeftPart(UriPartial.Authority)
                origin.GetLeftPart(UriPartial.Authority)
            |]
        )

    Expect.equal
        (RequestGate.postShape
            (WebBindings.create origin "127.0.0.1" origin.Port)
            32
            duplicateOrigin)
        (Error GateFailure.OriginRejected)
        "Duplicated origin headers are not accepted"

    let crossSite = context ()
    crossSite.Request.Headers["Sec-Fetch-Site"] <- "cross-site"

    Expect.equal
        (RequestGate.postShape (WebBindings.create origin "127.0.0.1" origin.Port) 32 crossSite)
        (Error GateFailure.FetchMetadataRejected)
        "Cross-site fetch metadata is refused"

let private mediaAndLengthBoundaries () =
    let missingMedia = context ()
    missingMedia.Request.ContentType <- null

    Expect.equal
        (RequestGate.postShape (WebBindings.create origin "127.0.0.1" origin.Port) 32 missingMedia)
        (Error GateFailure.UnsupportedMediaType)
        "Missing media type does not default to JSON"

    let parameterizedJson = context ()
    parameterizedJson.Request.ContentType <- "Application/Json; charset=utf-8"

    Expect.equal
        (RequestGate.postShape
            (WebBindings.create origin "127.0.0.1" origin.Port)
            32
            parameterizedJson)
        (Ok())
        "JSON permits a standard charset parameter without changing the media type"

    let atLimit = context ()
    atLimit.Request.ContentLength <- Nullable 32L

    Expect.equal
        (RequestGate.postShape (WebBindings.create origin "127.0.0.1" origin.Port) 32 atLimit)
        (Ok())
        "An exact endpoint Content-Length is admitted"

    let overLimit = context ()
    overLimit.Request.ContentLength <- Nullable 33L

    Expect.equal
        (RequestGate.postShape (WebBindings.create origin "127.0.0.1" origin.Port) 32 overLimit)
        (Error GateFailure.BodyTooLarge)
        "Oversized Content-Length is refused before body reads"

let private sessionTests () =
    let request = context ()
    Expect.equal (RequestGate.credentialMode request) CredentialMode.Missing "No credential"
    request.Request.Headers["Cookie"] <- "__Host-ClaimCoreSession=synthetic-opaque"

    Expect.equal
        (RequestGate.credentialMode request)
        CredentialMode.BrowserCookie
        "One browser credential is recognized"

    request.Request.Headers["Authorization"] <- "Bearer synthetic-token"
    Expect.equal (RequestGate.credentialMode request) CredentialMode.Mixed "Mixed paths fail closed"

let private antiforgeryTests () =
    let rejecting =
        { new IAntiforgery with
            member _.GetAndStoreTokens _ = invalidOp "Not used"
            member _.GetTokens _ = invalidOp "Not used"
            member _.IsRequestValidAsync _ = Task.FromResult(false)

            member _.ValidateRequestAsync _ =
                Task.FromException(AntiforgeryValidationException("Synthetic"))

            member _.SetCookieTokenAndHeader _ = ()
        }

    Expect.equal
        (RequestGate.validateAntiforgery rejecting (context ())
         |> Async.AwaitTask
         |> Async.RunSynchronously)
        (Error GateFailure.AntiforgeryRejected)
        "CSRF failure is classified before endpoint parsing"

[<Tests>]
let tests =
    testList
        "HTTP request gates"
        [
            testCase "[CC-WEB-001] admits only the exact local JSON shape" jsonShapeTests
            testCase
                "[CC-WEB-001] keeps the mutable browser session registry server-side"
                sessionTests
            testCase "[CC-WEB-001] returns typed CSRF gate refusal" antiforgeryTests
            testCase
                "[CC-WEB-001] rejects absent and remote peers or wrong host authority"
                connectionRefusals
            testCase
                "[CC-WEB-001] requires one exact browser origin and same-origin metadata"
                browserOriginRefusals
            testCase
                "[CC-WEB-001] distinguishes media and exact declared body-size boundaries"
                mediaAndLengthBoundaries
        ]
