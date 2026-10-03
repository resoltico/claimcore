module ClaimCore.Tests.RemoteTlsTests

open System
open Expecto
open ClaimCore.Cli

let private trustRootScope () =
    match RemoteTls.Create(None, Uri("https://remote.example.test/")) with
    | Error _ -> failtest "System trust must be available for remote HTTPS"
    | Ok client ->
        use _owned = client :> IDisposable

        Expect.isTrue
            client.Handler.CheckCertificateRevocationList
            "Remote HTTPS checks certificate revocation"

    match
        RemoteTls.Create(Some "/private/synthetic-ca.pem", Uri("https://remote.example.test/"))
    with
    | Ok value ->
        (value :> IDisposable).Dispose()
        failtest "A local test root must not authorize a remote production endpoint"
    | Error reason ->
        Expect.equal reason "TLS_TRUST_ROOT_SCOPE_INVALID" "No remote custom trust root"

let tests =
    testList
        "CLI TLS trust"
        [ testCase "private test trust root is confined to loopback" trustRootScope ]
