module ClaimCore.Tests.PropertyHarnessTests

open Expecto
open Hedgehog
open Hedgehog.FSharp
open ClaimCore.Tests.PropertyHarness

let private configurationTests =
    testList
        "complete deterministic properties"
        [
            testCase "every property executes 200 cases with sequences bounded to 20" (fun () ->
                let mutable executions = 0

                let counted =
                    property {
                        let! _ = Gen.constant ()
                        executions <- executions + 1
                        return true
                    }

                run "CC-PROP-TEXT-001" counted
                Expect.equal executions 200 "Every case executes"
                Expect.equal (sequenceMaximum ()) 20 "Stateful sequence bound"

                Expect.throws
                    (fun () -> run "unregistered-property" counted)
                    "An unregistered property cannot claim qualification")
            testCase "the same property uses the same seed and rejects a false result" (fun () ->
                let samples () =
                    let values = ResizeArray<int>()

                    let sampled =
                        property {
                            let! value = Gen.int32 (Range.constant 0 1000000)
                            values.Add(value)
                            return true
                        }

                    run "CC-PROP-AMOUNT-001" sampled
                    List.ofSeq values

                Expect.isTrue (samples () = samples ()) "Deterministic property seed"

                let rejected =
                    property {
                        let! _ = Gen.constant ()
                        return false
                    }

                Expect.throws
                    (fun () -> run "CC-PROP-TEXT-001" rejected)
                    "A false property cannot be counted as passing")
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
                let rendered = failureText "CC-PROP-TEXT-001" report
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
let tests =
    testList "property harness policy" [ configurationTests; diagnosticTests ]
