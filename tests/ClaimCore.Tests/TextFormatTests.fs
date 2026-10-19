module ClaimCore.Tests.TextFormatTests

open System
open Expecto
open ClaimCore.Domain
open ClaimCore.Tests.Fixtures

let private restored name =
    let original = opened () |> Case.snapshot

    let fields =
        { viewFields original with
            ClaimantName = name
            InsurerName = name
        }

    Case.ofAccepted (snapshot original.Revision fields)

let private format name =
    let input =
        { registration with
            ClaimantName = name
            InsurerName = name
        }

    Case.validateRequest (request 0L (BusinessCommand.Open input))

let private formatBoundaries () =
    for value in [ String('a', 1024); String.replicate 256 "😀"; String('\u0001', 1024) ] do
        Expect.isOk
            (restored value)
            "Accepted history is judged by format, independently of admission"

        Expect.isOk (format value) "Request format uses the same permanent byte bound"

    for value in [ String('a', 1025); String.replicate 257 "😀"; String('\u0001', 1025) ] do
        Expect.isError (restored value) "History cannot exceed the byte grammar"
        Expect.isError (format value) "New requests cannot exceed the byte grammar"

let private admissionInsideFormat () =
    Expect.isLessThanOrEqual
        (4 * TextAdmission.MaximumCharacters)
        TextFormat.MaximumFreeTextBytes
        "Every admitted scalar sequence fits the permanent byte bound"

    let name = String.replicate 200 "😀"

    let input =
        { registration with
            ClaimantName = name
            InsurerName = name
        }

    Expect.isOk
        (Case.decide today (request 0L (BusinessCommand.Open input)) None)
        "800 UTF-8 bytes admit"

    let input = { input with ClaimantName = name + "😀" }

    Expect.isOk
        (Case.validateRequest (request 0L (BusinessCommand.Open input)))
        "Format still admits"

    Expect.isError
        (Case.decide today (request 0L (BusinessCommand.Open input)) None)
        "Admission refuses"

let private stewardshipBoundaries () =
    for act in [ StewardshipAct.Void; StewardshipAct.Reinstate ] do
        let proposed value =
            { request 1L BusinessCommand.Close with
                Act = CaseAct.Stewardship(act value)
            }

        Expect.isOk
            (Case.validateRequest (proposed (String('a', 1024))))
            "Reason format boundary admits"

        Expect.isError
            (Case.validateRequest (proposed (String('a', 1025))))
            "Reason format boundary refuses"

[<Tests>]
let tests =
    testList
        "permanent text byte bounds"
        [
            testCase "names use UTF-8 byte boundaries on requests and restoration" formatBoundaries
            testCase
                "new assertions retain the scalar admission limit inside format"
                admissionInsideFormat
            testCase "both reason-bearing acts enforce the format bound" stewardshipBoundaries
        ]
