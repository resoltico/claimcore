module ClaimCore.Tests.DomainValidationTests

open ClaimCore.TestSupport


open Expecto
open ClaimCore.Domain
open ClaimCore.Tests.CaseBuilders

let private snapshot state =
    ClaimCore.Domain.Aggregates.CaseFamily.snapshot state |> Option.get

let private isInvalid =
    function
    | Error(Rejection.Input _) -> true
    | _ -> false

let private opening overrides =
    let values = ClaimCore.Tests.TaskBuilders.openingInputs.Values

    let values =
        overrides
        |> List.fold
            (fun values (field, value) ->
                Map.add
                    (TaskInputId.Target(InputTarget.Field field))
                    (InputValue.Text value)
                    values)
            values

    let input = InputMap.create values Map.empty |> Option.get

    ClaimCore.Domain.Tasks.Case.Open.decide
        (ClaimCore.Tests.TaskBuilders.context 1)
        ClaimCore.Tests.TaskBuilders.environment
        None
        (Binding.CaseNew Revision.zero)
        input
    |> Result.bind (fun changes ->
        changes
        |> List.fold
            (fun state change ->
                state
                |> Result.bind (fun state ->
                    ClaimCore.Domain.Aggregates.CaseFamily.apply state change
                    |> Result.map Some
                    |> Result.mapError Rejection.InvalidState))
            (Ok None))
    |> Result.map (Option.get >> snapshot)

let private chronologyTests =
    testList
        "chronology"
        [
            testCase "decision before notification is rejected" (fun () ->
                Expect.isTrue
                    (opened ()
                     |> apply
                         "case.decide"
                         (ClaimCore.Tests.TaskBuilders.decisionInputs "2026-08-02" "750" "EUR")
                     |> isInvalid)
                    "Chronology")
            testCase "payment before decision is rejected" (fun () ->
                Expect.isTrue
                    (decided ()
                     |> apply
                         "case.recordPayment"
                         (ClaimCore.Tests.TaskBuilders.paymentInputs "2026-08-14" "750" "EUR")
                     |> isInvalid)
                    "Chronology")
            testCase "future payment is rejected" (fun () ->
                Expect.isTrue
                    (decided ()
                     |> apply
                         "case.recordPayment"
                         (ClaimCore.Tests.TaskBuilders.paymentInputs "2026-09-08" "750" "EUR")
                     |> isInvalid)
                    "No future payment recorded as done")
        ]

let private restorationTests =
    testList
        "restoration integrity"
        [
            testCase "retained snapshots keep the exact-integer format bound" (fun () ->
                let original = snapshot (opened ())

                let terminal =
                    { original with
                        Revision = CaseFixtures.revision 9007199254740991L
                    }

                Expect.isOk
                    (ClaimCore.Domain.Aggregates.CaseFamily.validateSnapshot terminal)
                    "The format boundary remains readable"

                Expect.isNone (Revision.create 9007199254740992L) "Beyond safe range")
        ]

let private precisionAndRoundTripTests =
    testList
        "precision and round trips"
        [
            testCase "maximum amount stays exact" (fun () ->
                let maximum = "999999999999999999.9999"

                let claim = opening [ BusinessField.ClaimedAmount, maximum ] |> accepted

                Expect.equal
                    (Registration.claimed ((viewFields claim).Registration) |> Money.format)
                    maximum
                    "No rounding")
            testCase "all supported state views rehydrate" (fun () ->
                for state in
                    [
                        opened ()
                        decided ()
                        paid ()
                        opened () |> apply "case.close" InputMap.empty |> accepted
                    ] do
                    let view = snapshot state

                    Expect.isTrue
                        ((ClaimCore.Domain.Aggregates.CaseFamily.validateSnapshot view
                          |> Result.map (fun () -> view))
                            =
                            Ok view)
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
                    Expect.isTrue
                        (opening [ BusinessField.ClaimedAmount, value ] |> isInvalid)
                        "Exact decimal grammar")
        ]

let private dateGrammarTests =
    testList
        "date grammar"
        [
            for index, value in
                [ "2026-02-30"; "2026-8-01"; "01/08/2026"; "+10000-01-01" ] |> List.indexed do
                testCase $"invalid date case {index + 1:D2}" (fun () ->
                    Expect.isTrue
                        (opening [ BusinessField.IncidentDate, value ] |> isInvalid)
                        "Calendar grammar")
        ]

let private currencyGrammarTests =
    testList
        "currency grammar"
        [
            for index, value in [ ""; " EUR"; "eur"; "EURO"; "€"; "EUR\n" ] |> List.indexed do
                testCase $"invalid currency case {index + 1:D2}" (fun () ->
                    Expect.isTrue
                        (opening [ BusinessField.ClaimedCurrency, value ] |> isInvalid)
                        "Currency grammar")
        ]

let private authoredUnicodeRoundTrip () =
    for value in [ "نام‌خانوادگی"; "اسم‍"; "é"; "A️" ] do
        let snapshot = opening [ BusinessField.ClaimantName, value ] |> accepted

        Expect.equal
            (Registration.claimant ((viewFields snapshot).Registration) |> PartyName.format)
            value
            "Authored scalar content remains exact."

        Expect.equal
            (ClaimCore.Domain.Aggregates.CaseFamily.validateSnapshot snapshot
             |> Result.map (fun () -> snapshot))
            (Ok snapshot)
            "Historical restoration preserves the same text contract."

[<Tests>]
let tests =
    testList
        "domain validation"
        [
            testCase
                "business Unicode preserves joining and authored orthographic selectors through restoration"
                authoredUnicodeRoundTrip
            chronologyTests
            restorationTests
            precisionAndRoundTripTests
            decimalGrammarTests
            dateGrammarTests
            currencyGrammarTests
        ]
