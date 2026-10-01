module ClaimCore.Tests.IssuerIdentityTests

open System
open System.Text
open Expecto
open ClaimCore.Cli

let private metadata issuer =
    $"""{{"issuer":"{issuer}","authorization_endpoint":"https://identity.example.test/auth/","token_endpoint":"https://identity.example.test/token/","jwks_uri":"https://identity.example.test/keys/"}}"""
    |> Encoding.UTF8.GetBytes

let private exactDiscovery () =
    for source in
        [
            "https://identity.example.test/"
            "https://identity.example.test/realm/"
            "https://identity.example.test/realm"
        ] do
        let issuer = Uri source

        Expect.isOk
            (OidcClient.parseDiscovery issuer (metadata source))
            "Root and path issuers retain their exact identity"

        let different =
            if source.EndsWith("/", StringComparison.Ordinal) then
                source.TrimEnd('/')
            else
                source + "/"

        Expect.isError
            (OidcClient.parseDiscovery issuer (metadata different))
            "A slash-different issuer is not substituted"

let tests =
    testCase "[CC-AUTH-001] CLI discovery compares the complete issuer identity" exactDiscovery
