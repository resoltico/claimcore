module ClaimCore.Tests.DomainValidationTests

open ClaimCore.TestSupport

open ClaimCore.Protocol
open ClaimCore.Json

open Expecto
open ClaimCore.Domain
open ClaimCore.Tests.Fixtures

let private availableActs record =
    Case.eligibleActs record |> List.map CaseActKind.token

let private chronologyTests =
    testList
        "chronology"
        [
            testCase "decision before notification is rejected" (fun () ->
                Expect.isTrue
                    (opened ()
                     |> apply (
                         AuthoredCaseAct.Decide(
                             ClaimCore.TestSupport.Inputs.decisionWith
                                 [ BusinessField.PaymentDecisionDate, ("2026-08-02") ]
                                 (decision)
                         )
                     )
                     |> isInvalid)
                    "Chronology")
            testCase "payment before decision is rejected" (fun () ->
                Expect.isTrue
                    (decided ()
                     |> apply (
                         AuthoredCaseAct.RecordPayment(
                             ClaimCore.TestSupport.Inputs.paymentFrom
                                 [ BusinessField.PaymentDate, ("2026-08-14") ]
                         )
                     )
                     |> isInvalid)
                    "Chronology")
            testCase "future payment is rejected" (fun () ->
                Expect.isTrue
                    (decided ()
                     |> apply (
                         AuthoredCaseAct.RecordPayment(
                             ClaimCore.TestSupport.Inputs.paymentFrom
                                 [ BusinessField.PaymentDate, ("2026-09-08") ]
                         )
                     )
                     |> isInvalid)
                    "No future payment recorded as done")
        ]

let private restorationTests =
    testList
        "restoration integrity"
        [
            testCase "snapshot decoding cannot invent a decision for payment" (fun () ->
                let original = Case.snapshot (opened ())
                let bytes = Codec.encode ResponseCodecs.caseSnapshot original
                let root = System.Text.Json.Nodes.JsonNode.Parse(bytes) |> nonNull
                let fields = root["state"] |> nonNull |> fun state -> state["fields"] |> nonNull

                fields["payment"] <-
                    System.Text.Json.Nodes.JsonNode.Parse("{\"paymentDate\":\"2026-08-20\"}")

                let invalid = System.Text.Encoding.UTF8.GetBytes(root.ToJsonString())

                Expect.isError
                    (Codec.decode 100000 ResponseCodecs.caseSnapshot invalid)
                    "The invalid group pair is refused at the boundary")
            testCase "all six partial decision objects are rejected at the wire boundary" (fun () ->
                let text value = JsonValue.String value

                let registration =
                    Inputs.registrationWith
                        [
                            BusinessField.ClaimantName, "Synthetic party"
                            BusinessField.InsurerName, "Synthetic insurer"
                            BusinessField.ClaimedAmount, "1000"
                        ]
                        Inputs.registration
                    |> RequestCodecs.registration.Write

                let candidate mask =
                    JsonValue.Object
                        [
                            "caseReference", text "UNIT-001"
                            "status", text "OPENED"
                            "registration", registration
                            "decision",
                            JsonValue.Object
                                [
                                    if mask &&& 1 <> 0 then
                                        "paymentDecisionDate", text "2026-08-15"
                                    if mask &&& 2 <> 0 then
                                        "payableAmount", text "750"
                                    if mask &&& 4 <> 0 then
                                        "payableCurrency", text "EUR"
                                ]
                            "work",
                            JsonValue.Object
                                [
                                    "responsibleActor", JsonValue.Null
                                    "followUpDate", JsonValue.Null
                                ]
                            "payment", JsonValue.Null
                            "progress", text "DECIDED"
                        ]

                Expect.isOk
                    (ResponseCodecs.caseFields.Read "$" (candidate 7))
                    "The complete independent control admits"

                for mask in 1..6 do
                    Expect.isError
                        (ResponseCodecs.caseFields.Read "$" (candidate mask))
                        "A decision object requires all three members")
            testCase "retained snapshots keep the exact-integer format bound" (fun () ->
                let original = Case.snapshot (opened ())

                let terminal =
                    { original with
                        Revision = CaseFixtures.revision 9007199254740991L
                    }

                Expect.isOk (Case.ofAccepted terminal) "The format boundary remains readable"

                Expect.isNone (Revision.create 9007199254740992L) "Beyond safe range")
        ]

let private precisionAndRoundTripTests =
    testList
        "precision and round trips"
        [
            testCase "maximum amount stays exact" (fun () ->
                let maximum = "999999999999999999.9999"

                let facts =
                    (ClaimCore.TestSupport.Inputs.registrationWith
                        [ BusinessField.ClaimedAmount, (maximum) ]
                        (registration))

                let claim =
                    CaseFixtures.decide today (request 0L (AuthoredCaseAct.Open facts)) None
                    |> accepted

                Expect.isTrue
                    (Money.format (
                        Registration.claimed ((Case.snapshot claim |> viewFields).Registration)
                    )
                        =
                        maximum)
                    "No rounding")
            testCase "all supported state views rehydrate" (fun () ->
                for state in
                    [
                        opened ()
                        decided ()
                        paid ()
                        opened () |> apply (AuthoredCaseAct.Bare BareAct.Close) |> accepted
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
                        (ClaimCore.TestSupport.Inputs.registrationWith
                            [ BusinessField.ClaimedAmount, (value) ]
                            (registration))

                    Expect.isTrue
                        (CaseFixtures.decide today (request 0L (AuthoredCaseAct.Open facts)) None
                         |> isInvalid)
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
                        (ClaimCore.TestSupport.Inputs.registrationWith
                            [ BusinessField.IncidentDate, (value) ]
                            (registration))

                    Expect.isTrue
                        (CaseFixtures.decide today (request 0L (AuthoredCaseAct.Open facts)) None
                         |> isInvalid)
                        "Calendar grammar")
        ]

let private currencyGrammarTests =
    testList
        "currency grammar"
        [
            for index, value in [ ""; " EUR"; "eur"; "EURO"; "€"; "EUR\n" ] |> List.indexed do
                testCase $"invalid currency case {index + 1:D2}" (fun () ->
                    let facts =
                        (ClaimCore.TestSupport.Inputs.registrationWith
                            [ BusinessField.ClaimedCurrency, (value) ]
                            (registration))

                    Expect.isTrue
                        (CaseFixtures.decide today (request 0L (AuthoredCaseAct.Open facts)) None
                         |> isInvalid)
                        "Currency grammar")
        ]

let private authoredUnicodeRoundTrip () =
    for value in [ "نام‌خانوادگی"; "اسم‍"; "é"; "A️" ] do
        let facts =
            (ClaimCore.TestSupport.Inputs.registrationWith
                [ BusinessField.ClaimantName, (value) ]
                (registration))

        let claim =
            CaseFixtures.decide today (request 0L (AuthoredCaseAct.Open facts)) None
            |> accepted

        let snapshot = Case.snapshot claim

        Expect.equal
            (PartyName.format (Registration.claimant ((viewFields snapshot).Registration)))
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
                "business Unicode preserves joining and authored orthographic selectors through restoration"
                authoredUnicodeRoundTrip
            chronologyTests
            restorationTests
            precisionAndRoundTripTests
            decimalGrammarTests
            dateGrammarTests
            currencyGrammarTests
        ]
