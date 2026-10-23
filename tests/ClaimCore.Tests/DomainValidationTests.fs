module ClaimCore.Tests.DomainValidationTests

open ClaimCore.Contract

open Expecto
open ClaimCore.Domain
open ClaimCore.Tests.Fixtures

let private availableActs record =
    Case.eligibleActs record |> List.map CaseActKinds.token

let private chronologyTests =
    testList
        "chronology"
        [
            testCase "decision before notification is rejected" (fun () ->
                Expect.isTrue
                    (opened ()
                     |> apply (
                         CaseAct.Decide
                             { decision with
                                 PaymentDecisionDate = "2026-08-02"
                             }
                     )
                     |> isInvalid)
                    "Chronology")
            testCase "payment before decision is rejected" (fun () ->
                Expect.isTrue
                    (decided ()
                     |> apply (CaseAct.RecordPayment { PaymentDate = "2026-08-14" })
                     |> isInvalid)
                    "Chronology")
            testCase "future payment is rejected" (fun () ->
                Expect.isTrue
                    (decided ()
                     |> apply (CaseAct.RecordPayment { PaymentDate = "2026-09-08" })
                     |> isInvalid)
                    "No future payment recorded as done")
        ]

let private restorationTests =
    testList
        "restoration integrity"
        [
            testCase "restore cannot invent a decision for payment" (fun () ->
                let original = Case.snapshot (opened ())

                let invalid =
                    snapshot
                        original.Revision
                        { (viewFields original) with
                            PaymentDate = Some "2026-08-20"
                        }

                let result = Case.ofAccepted invalid |> Result.map Case.snapshot
                Expect.isTrue (result = Error RecordViolation.InvalidProgress) "Corruption")
            testCase "all six incomplete decision tuples are rejected on restoration" (fun () ->
                let original = Case.snapshot (opened ())

                for mask in 1..6 do
                    let fields =
                        { (viewFields original) with
                            PaymentDecisionDate =
                                if mask &&& 1 <> 0 then Some "2026-08-15" else None
                            PayableAmount = if mask &&& 2 <> 0 then Some "750" else None
                            PayableCurrency = if mask &&& 4 <> 0 then Some "EUR" else None
                        }

                    Expect.isError
                        (Case.ofAccepted (snapshot original.Revision fields))
                        "An entire tuple, never a partial tuple")
            testCase
                "safe-integer terminal snapshots remain readable and refuse a successor"
                (fun () ->
                    let original = Case.snapshot (opened ())

                    let terminal =
                        { original with
                            Revision = Revision.Maximum
                        }

                    let current = Case.ofAccepted terminal |> accepted
                    Expect.isEmpty (availableActs current) "No successor can be represented"

                    Expect.isTrue
                        ((Case.decide today (request Revision.Maximum CaseAct.Close) (Some current)
                          |> Result.map Case.snapshot)
                            =
                            Error Refusal.RevisionExhausted)
                        "Terminal state refuses without arithmetic"

                    Expect.isError
                        (Case.ofAccepted
                            { original with
                                Revision = Revision.Maximum + 1L
                            })
                        "Beyond safe range")
        ]

let private precisionAndRoundTripTests =
    testList
        "precision and round trips"
        [
            testCase "maximum amount stays exact" (fun () ->
                let maximum = "999999999999999999.9999"

                let facts =
                    { registration with
                        ClaimedAmount = maximum
                    }

                let claim = Case.decide today (request 0L (CaseAct.Open facts)) None |> accepted

                Expect.isTrue
                    ((Case.snapshot claim |> viewFields).ClaimedAmount = maximum)
                    "No rounding")
            testCase "all supported state views rehydrate" (fun () ->
                for state in
                    [
                        opened ()
                        decided ()
                        paid ()
                        opened () |> apply CaseAct.Close |> accepted
                    ] do
                    let view = Case.snapshot state

                    Expect.isTrue
                        ((Case.ofAccepted view |> accepted |> Case.snapshot) = view)
                        "Round trip")
        ]

let private decimalGrammarTests =
    testList
        "decimal grammar"
        [
            for index, value in
                [
                    "-1"
                    "+1"
                    "1,20"
                    "01"
                    "1e2"
                    "1.00001"
                    "1000000000000000000"
                    ".5"
                    "NaN"
                    "Infinity"
                    "1."
                    "1\n"
                ]
                |> List.indexed do
                testCase $"invalid decimal case {index + 1:D2}" (fun () ->
                    let facts =
                        { registration with
                            ClaimedAmount = value
                        }

                    Expect.isTrue
                        (Case.decide today (request 0L (CaseAct.Open facts)) None |> isInvalid)
                        "Exact decimal grammar")
        ]

let private dateGrammarTests =
    testList
        "date grammar"
        [
            for index, value in
                [ "2026-02-30"; "2026-8-01"; "01/08/2026"; "+10000-01-01" ] |> List.indexed do
                testCase $"invalid date case {index + 1:D2}" (fun () ->
                    let facts =
                        { registration with
                            IncidentDate = value
                        }

                    Expect.isTrue
                        (Case.decide today (request 0L (CaseAct.Open facts)) None |> isInvalid)
                        "Calendar grammar")
        ]

let private currencyGrammarTests =
    testList
        "currency grammar"
        [
            for index, value in [ ""; " EUR"; "eur"; "EURO"; "€"; "EUR\n" ] |> List.indexed do
                testCase $"invalid currency case {index + 1:D2}" (fun () ->
                    let facts =
                        { registration with
                            ClaimedCurrency = value
                        }

                    Expect.isTrue
                        (Case.decide today (request 0L (CaseAct.Open facts)) None |> isInvalid)
                        "Currency grammar")
        ]

let private authoredUnicodeRoundTrip () =
    for value in [ "نام\u200Cخانوادگی"; "اسم\u200D"; "e\u0301"; "A\uFE0F" ] do
        let facts =
            { registration with
                ClaimantName = value
            }

        let claim = Case.decide today (request 0L (CaseAct.Open facts)) None |> accepted

        let snapshot = Case.snapshot claim

        Expect.equal
            (viewFields snapshot).ClaimantName
            value
            "Authored scalar content remains exact."

        Expect.equal
            (Case.ofAccepted snapshot |> Result.map Case.snapshot)
            (Ok snapshot)
            "Historical restoration preserves the same text contract."

[<Tests>]
let tests =
    testList
        "domain validation"
        [
            testCase
                "[CC-DOM-001] business Unicode preserves joining and authored orthographic selectors through restoration"
                authoredUnicodeRoundTrip
            chronologyTests
            restorationTests
            precisionAndRoundTripTests
            decimalGrammarTests
            dateGrammarTests
            currencyGrammarTests
        ]
