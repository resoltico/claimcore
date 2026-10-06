module ClaimCore.WebTests.BindingAdmissionTests

open System
open System.Net
open Expecto
open Microsoft.AspNetCore.Http
open ClaimCore.Web

let private request (origin: string) (peer: string) =
    let context = DefaultHttpContext()
    context.Connection.RemoteIpAddress <- IPAddress.Parse(peer)
    context.Request.Headers.Host <- origin
    context.Request.Headers.Origin <- "https://claims.example.test"
    context.Request.ContentType <- "application/json"
    context

let private peerBoundary () =
    let origin = Uri("https://claims.example.test")
    let local = WebBindings.create origin "127.0.0.1" 5443
    let container = WebBindings.create origin "0.0.0.0" 5443
    let forwarded = request "claims.example.test" "192.0.2.20"

    Expect.isFalse
        (Admission.trustedConnection local forwarded)
        "Loopback binding refuses remote peers"

    Expect.isTrue
        (Admission.trustedConnection container forwarded)
        "Explicit container binding permits forwarded TCP peers"

    Expect.equal
        (Admission.postShape container RequestBody.Json 32 forwarded)
        (Ok())
        "Exact public origin is preserved"

    forwarded.Request.Headers.Host <- "foreign.example.test"

    Expect.isFalse
        (Admission.trustedConnection container forwarded)
        "Container publication never permits foreign Host"

    forwarded.Request.Headers.Host <- "claims.example.test"
    forwarded.Request.Headers.Origin <- "https://foreign.example.test"

    Expect.equal
        (Admission.postShape container RequestBody.Json 32 forwarded)
        (Error AdmissionFailure.OriginRejected)
        "Origin remains exact"

    forwarded.Connection.RemoteIpAddress <- null
    Expect.isFalse (Admission.trustedConnection container forwarded) "Unknown peer is refused"

let private invalidBindings () =
    let origin = Uri("https://localhost:5443")

    for address in [ "not-an-address"; "255.255.255.255" ] do
        Expect.throws
            (fun () -> WebBindings.create origin address 5443 |> ignore)
            "Invalid listener address"

    for port in [ 0; 65536 ] do
        Expect.throws
            (fun () -> WebBindings.create origin "127.0.0.1" port |> ignore)
            "Invalid listener port"

let tests =
    testList
        "listener and public authority"
        [
            testCase
                "[CC-WEB-001] explicit container binding retains Host and origin refusal"
                peerBoundary
            testCase "[CC-WEB-001] listener rejects invalid address and port" invalidBindings
        ]
