module ClaimCore.Tests.DomainValidationTests

open Expecto
open ClaimCore.Domain
open ClaimCore.Tests.Fixtures

let private chronologyTests =
    testList
        "chronology"
        [
            testCase "decision before notification is rejected" (fun () ->
                Expect.isTrue
                    (opened ()
                     |> apply (
                         Command.Decide
                             { decision with
                                 PaymentDecisionDate = "2026-08-02"
                             }
                     )
                     |> isInvalid)
                    "Chronology")
            testCase "payment before decision is rejected" (fun () ->
                Expect.isTrue
                    (decided () |> apply (Command.RecordPayment "2026-08-14") |> isInvalid)
                    "Chronology")
            testCase "future payment is rejected" (fun () ->
                Expect.isTrue
                    (decided () |> apply (Command.RecordPayment "2026-09-08") |> isInvalid)
                    "No future payment recorded as done")
        ]

let private restorationTests =
    testList
        "restoration integrity"
        [
            testCase "restore cannot invent a decision for payment" (fun () ->
                let original = Claim.view (opened ())

                let invalid =
                    { original with
                        Fields =
                            { original.Fields with
                                PaymentDate = Some "2026-08-20"
                            }
                    }

                let result = Claim.restore invalid |> Result.map Claim.view
                Expect.isTrue (result = Error DomainError.DecisionRequired) "Corruption")
            testCase "all six incomplete decision tuples are rejected on restoration" (fun () ->
                let original = Claim.view (opened ())

                for mask in 1..6 do
                    let fields =
                        { original.Fields with
                            PaymentDecisionDate =
                                if mask &&& 1 <> 0 then Some "2026-08-15" else None
                            PayableAmount = if mask &&& 2 <> 0 then Some "750" else None
                            PayableCurrency = if mask &&& 4 <> 0 then Some "EUR" else None
                        }

                    Expect.isError
                        (Claim.restore { original with Fields = fields })
                        "An entire tuple, never a partial tuple")
            testCase "revision ceiling rejects terminal snapshots and prevents overflow" (fun () ->
                let original = Claim.view (opened ())

                let penultimate =
                    { original with
                        Version = System.Int64.MaxValue - 1L
                    }

                let terminal =
                    { original with
                        Version = System.Int64.MaxValue
                    }

                Expect.isError (Claim.restore terminal) "Terminal revision is not accepted state"

                let current = Claim.restore penultimate |> accepted
                Expect.isEmpty (Claim.availableCommands current) "No unrepresentable next action"

                Expect.isTrue
                    (Claim.decide today (request penultimate.Version Command.Close) (Some current)
                     |> isInvalid)
                    "A transition cannot reach Int64.MaxValue")
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

                let claim = Claim.decide today (request 0L (Command.Open facts)) None |> accepted
                Expect.isTrue ((Claim.view claim).Fields.ClaimedAmount = maximum) "No rounding")
            testCase "all supported state views rehydrate" (fun () ->
                for state in
                    [
                        opened ()
                        decided ()
                        paid ()
                        opened () |> apply Command.Close |> accepted
                    ] do
                    let view = Claim.view state

                    Expect.isTrue
                        ((Claim.restore view |> accepted |> Claim.view) = view)
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
                        (Claim.decide today (request 0L (Command.Open facts)) None |> isInvalid)
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
                        (Claim.decide today (request 0L (Command.Open facts)) None |> isInvalid)
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
                        (Claim.decide today (request 0L (Command.Open facts)) None |> isInvalid)
                        "Currency grammar")
        ]

let tests =
    testList
        "domain validation"
        [
            chronologyTests
            restorationTests
            precisionAndRoundTripTests
            decimalGrammarTests
            dateGrammarTests
            currencyGrammarTests
        ]
