module ClaimCore.Tests.InputIdentityTests

open System.Text
open Expecto
open ClaimCore.Json
open ClaimCore.Protocol

[<Tests>]
let tests =
    testCase "an unknown principal kind is rejected at its wire input" (fun () ->
        let raw =
            """{"kind":"UNDECLARED","issuer":"https://issuer.example/","subject":"synthetic-subject"}"""

        match
            Codec.decode
                Limits.MaximumRequestBytes
                RequestCodecs.principal
                (Encoding.UTF8.GetBytes raw)
        with
        | Error problem ->
            Expect.equal problem.Path "$.kind" "An unknown kind cannot become a subject refusal"
        | Ok _ -> failtest "Undeclared principal kinds cannot reach Domain."

        let role =
            """{"kind":"GRANT","role":"UNDECLARED","scope":{"kind":"INSTALLATION"}}"""

        match
            Codec.decode
                Limits.MaximumRequestBytes
                RequestCodecs.authorityAct
                (Encoding.UTF8.GetBytes role)
        with
        | Error problem ->
            Expect.equal problem.Path "$.role" "An undeclared role is a protocol fault"
        | Ok _ -> failtest "Undeclared roles cannot reach Domain.")
