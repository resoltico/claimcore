module ClaimCore.Tests.PropertyHarnessTests

open Expecto
open Hedgehog
open Hedgehog.FSharp
open ClaimCore.Tests.PropertyHarness

let private map values = Map.ofList values

let private configurationTests =
    testList
        "property configuration"
        [
            testCase "required and extended profiles are explicit" (fun () ->
                Expect.isOk (parsePlan Map.empty) "Required default"

                Expect.isOk
                    (parsePlan (
                        map
                            [
                                "CLAIMCORE_PROPERTY_PROFILE", "extended"
                                "CLAIMCORE_PROPERTY_BASE_SEED", "42"
                            ]
                    ))
                    "Extended seed")
            testCase "partial and additional recheck inputs fail closed" (fun () ->
                Expect.isError
                    (parsePlan (map [ "CLAIMCORE_PROPERTY_PROFILE", "recheck" ]))
                    "Partial recheck"

                Expect.isError
                    (parsePlan (
                        map
                            [
                                "CLAIMCORE_PROPERTY_PROFILE", "required"
                                "CLAIMCORE_PROPERTY_BASE_SEED", "1"
                            ]
                    ))
                    "Required seed override")
        ]

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
                let rendered = failureText "CC-PROP-TEXT-001" Required report
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

let tests =
    testList "property harness policy" [ configurationTests; diagnosticTests ]
