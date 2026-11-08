module ClaimCore.Tests.PropertyHarnessTests

open Expecto
open Hedgehog
open Hedgehog.FSharp
open ClaimCore.Tests.PropertyHarness

let private diagnosticTests =
    testList
        "failure diagnostic privacy"
        [
            testCase "property failures omit generated payload and control canaries" (fun () ->
                let canary = "CLAIMANT-CANARY\u0000{\"request\":\"payload\"}"

                let unsafeProperty =
                    property {
                        let! _ = Gen.constant canary
                        return false
                    }

                let report = Property.reportBool unsafeProperty
                let rendered = failureText "business text" report
                Expect.isFalse (rendered.Contains(canary)) "No generated payload"
                Expect.isFalse (rendered.Contains('\u0000')) "No control character"
                Expect.stringContains rendered "recheck=" "Safe replay material remains")
            testCase "structural assertion failures omit compared claimant values" (fun () ->
                let canary = "CLAIMANT-TRX-CANARY"

                let diagnostic =
                    try
                        Expect.isTrue ([ canary ] = [ "different" ]) "Payload-safe comparison"
                        failtest "The negative comparison unexpectedly passed."
                    with error ->
                        error.ToString()

                Expect.isFalse
                    (diagnostic.Contains(canary))
                    "Compared data stays out of diagnostics")
        ]

[<Tests>]
let tests = testList "property diagnostic privacy" [ diagnosticTests ]
