module ClaimCore.WebTests.AdmissionEdgeTests

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
        (Admission.postShape origin RequestBody.Json 32 accepted)
        (Ok())
        "JSON admission accepts the exact local shape"

    let duplicateHost = context ()

    duplicateHost.Request.Headers["Host"] <- StringValues([| origin.Authority; origin.Authority |])

    Expect.equal
        (Admission.postShape origin RequestBody.Json 32 duplicateHost)
        (Error AdmissionFailure.UntrustedConnection)
        "Host header cardinality is exact"

    let wrongMedia = context ()
    wrongMedia.Request.ContentType <- "application/jsonx"

    Expect.equal
        (Admission.postShape origin RequestBody.Json 32 wrongMedia)
        (Error AdmissionFailure.UnsupportedMediaType)
        "JSON media is not prefix matched"

let private rawShapeTests () =
    let envelope = context ()
    envelope.Request.ContentType <- "application/vnd.claimcore.recovery+json"
    envelope.Request.ContentLength <- Nullable 131072L

    Expect.equal
        (Admission.postShape
            origin
            (RequestBody.Raw "application/vnd.claimcore.recovery+json")
            131072
            envelope)
        (Ok())
        "The recovery envelope uses its exact raw media type and bound"

    envelope.Request.ContentType <- "application/vnd.claimcore.recovery+json; charset=utf-8"

    Expect.equal
        (Admission.postShape
            origin
            (RequestBody.Raw "application/vnd.claimcore.recovery+json")
            131072
            envelope)
        (Error AdmissionFailure.UnsupportedMediaType)
        "Raw artifacts do not accept parameterized media types"

let private connectionRefusals () =
    let absentPeer = context ()
    absentPeer.Connection.RemoteIpAddress <- null

    Expect.equal
        (Admission.postShape origin RequestBody.Json 32 absentPeer)
        (Error AdmissionFailure.UntrustedConnection)
        "An absent remote address is never assumed local"

    let remotePeer = context ()
    remotePeer.Connection.RemoteIpAddress <- IPAddress.Parse("192.0.2.1")

    Expect.equal
        (Admission.postShape origin RequestBody.Json 32 remotePeer)
        (Error AdmissionFailure.UntrustedConnection)
        "A non-loopback peer cannot submit work"

    let wrongHost = context ()
    wrongHost.Request.Headers.Host <- "attacker.example"

    Expect.equal
        (Admission.postShape origin RequestBody.Json 32 wrongHost)
        (Error AdmissionFailure.UntrustedConnection)
        "A loopback peer cannot redirect the host authority"

let private browserOriginRefusals () =
    let missingOrigin = context ()
    missingOrigin.Request.Headers.Remove("Origin") |> ignore

    Expect.equal
        (Admission.postShape origin RequestBody.Json 32 missingOrigin)
        (Error AdmissionFailure.OriginRejected)
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
        (Admission.postShape origin RequestBody.Json 32 duplicateOrigin)
        (Error AdmissionFailure.OriginRejected)
        "Duplicated origin headers are not accepted"

    let crossSite = context ()
    crossSite.Request.Headers["Sec-Fetch-Site"] <- "cross-site"

    Expect.equal
        (Admission.postShape origin RequestBody.Json 32 crossSite)
        (Error AdmissionFailure.FetchMetadataRejected)
        "Cross-site fetch metadata is refused"

let private mediaAndLengthBoundaries () =
    let missingMedia = context ()
    missingMedia.Request.ContentType <- null

    Expect.equal
        (Admission.postShape origin RequestBody.Json 32 missingMedia)
        (Error AdmissionFailure.UnsupportedMediaType)
        "Missing media type does not default to JSON"

    let parameterizedJson = context ()
    parameterizedJson.Request.ContentType <- "Application/Json; charset=utf-8"

    Expect.equal
        (Admission.postShape origin RequestBody.Json 32 parameterizedJson)
        (Ok())
        "JSON permits a standard charset parameter without changing the media type"

    let atLimit = context ()
    atLimit.Request.ContentLength <- Nullable 32L

    Expect.equal
        (Admission.postShape origin RequestBody.Json 32 atLimit)
        (Ok())
        "An exact endpoint Content-Length is admitted"

    let overLimit = context ()
    overLimit.Request.ContentLength <- Nullable 33L

    Expect.equal
        (Admission.postShape origin RequestBody.Json 32 overLimit)
        (Error AdmissionFailure.BodyTooLarge)
        "Oversized Content-Length is refused before body reads"

let private sessionTests () =
    let sessions = SessionRegistry(TimeSpan.FromMinutes(1.), TimeSpan.FromMinutes(2.))
    let now = DateTimeOffset.UtcNow
    let id = sessions.Create(now)

    Expect.isTrue (sessions.IsCurrent(id, now.AddSeconds(1.))) "A live registry entry is current"
    sessions.Revoke id
    Expect.isFalse (sessions.IsCurrent(id, now)) "Logout revokes the registry entry"

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
        (Admission.validateAntiforgery rejecting (context ())
         |> Async.AwaitTask
         |> Async.RunSynchronously)
        (Error AdmissionFailure.AntiforgeryRejected)
        "CSRF failure is classified before endpoint parsing"

let tests =
    testList
        "Web v2 admission"
        [
            testCase "[CC-WEB-001] admits only the exact local JSON shape" jsonShapeTests
            testCase
                "[CC-WEB-001] gives raw recovery artifacts independent exact media bounds"
                rawShapeTests
            testCase
                "[CC-WEB-001] keeps the mutable browser session registry server-side"
                sessionTests
            testCase "[CC-WEB-001] returns typed CSRF admission refusal" antiforgeryTests
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
