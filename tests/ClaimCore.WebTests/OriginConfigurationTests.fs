module ClaimCore.WebTests.OriginConfigurationTests

open Expecto
open ClaimCore.Web

let private origin () =
    let valid = Configuration.parseOrigin "https://LOCALHOST:5443"
    Expect.equal valid.Port 5443 "Explicit loopback HTTPS origin"

    Expect.equal
        (Configuration.parseOrigin "https://claims.example.test").Host
        "claims.example.test"
        "Explicit service DNS identity"

    for value in
        [
            "http://localhost:5443"
            "https://localhost:5443/path"
            "https://user@localhost:5443"
            "https://localhost:5443/#fragment"
            "not a URI"
        ] do
        Expect.throws (fun () -> Configuration.parseOrigin value |> ignore) "Invalid origin"

let tests =
    testCase
        "[CC-WEB-001] accepts one explicit HTTPS authority and rejects ambiguous origins"
        origin
