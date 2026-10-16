module ClaimCore.AcceptanceTests.IdentityReadinessTests

open System.Text
open Expecto

let private metadataControls () =
    let issuer = "https://identity.example.test/realms/synthetic"

    for metadata in
        [
            "{}"
            "{"
            "null"
            "{\"issuer\":1}"
            "{\"issuer\":\"https://identity.example.test/realms/other\"}"
        ] do
        Expect.isFalse
            (IdentityServer.metadataMatches
                issuer
                (System.ReadOnlyMemory(Encoding.UTF8.GetBytes metadata)))
            "Incomplete or different-realm metadata is not readiness"

    Expect.isTrue
        (IdentityServer.metadataMatches
            issuer
            (System.ReadOnlyMemory(Encoding.UTF8.GetBytes($"{{\"issuer\":\"{issuer}\"}}"))))
        "Only the imported expected realm is ready"

[<Tests>]
let tests =
    testCase "owned acceptance identity waits for the imported expected realm" metadataControls
