module ClaimCore.Tests.CommandIdentityIsolationTests

open System.Security.Cryptography
open Expecto
open ClaimCore.Application
open ClaimCore.Domain
open ClaimCore.Tests.Fixtures

let tests =
    testCase
        "[CC-APP-002] prepared canonical identity cannot be mutated through an accessor"
        (fun () ->
            let operation =
                request 0L (Command.Open registration) |> Operation.prepare |> accepted

            let original = Operation.canonicalRequest operation
            let returned = Operation.canonicalRequest operation
            returned[0] <- returned[0] ^^^ 1uy
            let retained = Operation.canonicalRequest operation
            Expect.sequenceEqual retained original "Private operation retains its exact bytes"

            Expect.equal
                (SHA256.HashData(retained) |> System.Convert.ToHexStringLower)
                (Operation.fingerprint operation)
                "Content and digest remain bound")
