module ClaimCore.WebTests.OriginConfigurationTests

open Expecto
open ClaimCore.Web

let private origin () =
    let valid = Configuration.parseOrigin "https://LOCALHOST:5443"
    Expect.equal valid.Port 5443 "Explicit loopback HTTPS origin"

    for value in
        [
            "http://localhost:5443"
            "https://example.test:5443"
            "https://localhost:5443/path"
            "https://user@localhost:5443"
            "https://localhost:5443/#fragment"
            "not a URI"
        ] do
        Expect.throws (fun () -> Configuration.parseOrigin value |> ignore) "Invalid origin"

let tests = testCase "[CC-WEB-001] accepts only one exact local HTTPS origin" origin
