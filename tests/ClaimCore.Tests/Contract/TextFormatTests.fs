module ClaimCore.Tests.TextFormatTests

open ClaimCore.TestSupport

open ClaimCore.Contract

open System
open Expecto
open ClaimCore.Domain
open ClaimCore.Tests.Fixtures

let private restored (name: string) =
    let encoded =
        opened () |> Case.snapshot |> ClaimCore.RecordFormat.CaseRecord.encodeSnapshot

    let root =
        System.Text.Json.Nodes.JsonNode.Parse(Text.Encoding.UTF8.GetString encoded)
        |> nonNull

    let state = root["state"] |> nonNull
    let fields = state["fields"] |> nonNull
    let registration = fields["registration"] |> nonNull
    registration["claimantName"] <- System.Text.Json.Nodes.JsonValue.Create name
    registration["insurerName"] <- System.Text.Json.Nodes.JsonValue.Create name

    root.ToJsonString()
    |> Text.Encoding.UTF8.GetBytes
    |> ClaimCore.RecordFormat.CaseRecord.decodeSnapshot

let private format name =
    let input =
        { registration with
            ClaimantName = name
            InsurerName = name
        }

    CaseFixtures.validate (request 0L (UnvalidatedCaseAct.Open input))

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
        (4 * PartyName.MaximumCharacters)
        Formats.MaximumFreeTextBytes
        "Every admitted scalar sequence fits the permanent byte bound"

    let name = String.replicate 200 "😀"

    let input =
        { registration with
            ClaimantName = name
            InsurerName = name
        }

    Expect.isOk
        (CaseFixtures.decide today (request 0L (UnvalidatedCaseAct.Open input)) None)
        "800 UTF-8 bytes admit"

    let input = { input with ClaimantName = name + "😀" }

    Expect.isOk
        (CaseFixtures.validate (request 0L (UnvalidatedCaseAct.Open input)))
        "Format still admits"

    Expect.isError
        (CaseFixtures.decide today (request 0L (UnvalidatedCaseAct.Open input)) None)
        "Admission refuses"

let private stewardshipBoundaries () =
    for act in [ UnvalidatedCaseAct.Void; UnvalidatedCaseAct.Reinstate ] do
        let proposed value =
            { request 1L UnvalidatedCaseAct.Close with
                Act = (act value)
            }

        Expect.isOk
            (CaseFixtures.validate (proposed (String('a', 1024))))
            "Reason format boundary admits"

        Expect.isError
            (CaseFixtures.validate (proposed (String('a', 1025))))
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
