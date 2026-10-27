module ClaimCore.Tests.InputIdentityTests

open Expecto
open ClaimCore.Domain

[<Tests>]
let tests =
    testCase "an unknown principal kind identifies the kind rather than the subject" (fun () ->
        let expected =
            Error(Refusal.InvalidInput(InputTarget.PrincipalKind, Violation.PrincipalFormat))

        Expect.equal
            (PrincipalKey.admit "UNDECLARED" "https://issuer.example/" "synthetic-subject")
            expected
            "Authored admission identifies the invalid kind"

        Expect.equal
            (PrincipalKey.fromStorage "UNDECLARED" "https://issuer.example/" "synthetic-subject")
            expected
            "The adjacent stored-kind parser identifies the same invalid input")
