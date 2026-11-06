module ClaimCore.Tests.InputAccumulationTests

open System
open Expecto
open Hedgehog
open Hedgehog.FSharp
open ClaimCore.Domain
open ClaimCore.Tests.PropertyHarness
open ClaimCore.TestSupport

let private fields =
    [
        BusinessField.IncidentDate
        BusinessField.IncidentNotificationDate
        BusinessField.IncidentCountry
        BusinessField.ClaimantName
        BusinessField.InsurerName
        BusinessField.ClaimedAmount
        BusinessField.ClaimedCurrency
    ]

let private request mask : UnvalidatedCaseRequest =
    let invalid index = mask &&& (1 <<< index) <> 0
    let value index valid = if invalid index then "" else valid
    let original = Inputs.registration

    {
        OperationId =
            if invalid 0 then
                Guid.Empty
            else
                Guid.Parse "70000000-0000-4000-8000-000000000001"
        CaseReference = value 1 "SYNTHETIC-ACCUMULATION"
        ExpectedRevision = if invalid 2 then -1L else 1L
        Act =
            CaseAct.AmendRegistration(
                Inputs.registrationUpdate (
                    ClaimCore.TestSupport.Inputs.registrationFrom
                        [
                            BusinessField.IncidentDate,
                            (value 3 (RegistrationInput.value BusinessField.IncidentDate (original)))
                            BusinessField.IncidentNotificationDate,
                            (value
                                4
                                (RegistrationInput.value
                                    BusinessField.IncidentNotificationDate
                                    (original)))
                            BusinessField.IncidentCountry,
                            (value
                                5
                                (RegistrationInput.value BusinessField.IncidentCountry (original)))
                            BusinessField.ClaimantName,
                            (value 6 (RegistrationInput.value BusinessField.ClaimantName (original)))
                            BusinessField.InsurerName,
                            (value 7 (RegistrationInput.value BusinessField.InsurerName (original)))
                            BusinessField.ClaimedAmount,
                            (value
                                8
                                (RegistrationInput.value BusinessField.ClaimedAmount (original)))
                            BusinessField.ClaimedCurrency,
                            (value
                                9
                                (RegistrationInput.value BusinessField.ClaimedCurrency (original)))
                        ]
                )
            )
    }

let private expected mask =
    [
        yield InputTarget.OperationId
        yield InputTarget.Field BusinessField.CaseReference
        yield InputTarget.ExpectedRevision
        yield! fields |> List.map InputTarget.Field
    ]
    |> List.indexed
    |> List.choose (fun (index, target) ->
        if mask &&& (1 <<< index) <> 0 then Some target else None)

let private targets =
    function
    | Ok _ -> []
    | Error(Refusal.InvalidInput issues) -> InputIssues.toList issues |> List.map _.Target
    | Error _ -> failtest "Permanent field parsing cannot produce a state refusal"

let private catalogRank target =
    let inputs =
        ClaimCore.Protocol.OperationCatalog.derive (
            ClaimCore.Protocol.CatalogOperation.Act(
                ClaimCore.Protocol.ActKind.Case CaseActKind.AmendRegistration
            )
        )
        |> fun bindings -> bindings.Inputs

    let token = InputTarget.token target

    inputs
    |> List.findIndex (fun input -> List.contains token input.Binding.DiagnosticTargets)

let private complete mask actual =
    Expect.equal actual (expected mask) "Exactly the invalid fields are reported in catalog order"

[<Tests>]
let tests =
    testList
        "accumulated input admission"
        [
            testCase "random invalid subsets report every field in catalog order" (fun () ->
                let property =
                    property {
                        let! mask = Gen.int32 (Range.constant 0 1023)
                        let actual = Case.parse (request mask) |> targets
                        let ranks = actual |> List.map catalogRank
                        return actual = expected mask && ranks = List.sort ranks
                    }

                run "input issue subsets" property)
            testCase "a short-circuit negative control fails completeness" (fun () ->
                let mask = 1023
                let actual = Case.parse (request mask) |> targets
                complete mask actual

                Expect.throws
                    (fun () -> complete mask (List.take 1 actual))
                    "Keeping only one issue loses required diagnostics")
            testCase "the issue list cannot be empty" (fun () ->
                Expect.isNone
                    (InputIssues.ofList [])
                    "No empty invalid-input result can be constructed")
        ]

[<Tests>]
let phase2 =
    testCase
        "phase two keeps every date/text issue in derived catalog order and within the wire bound"
        (fun () ->
            let before = ClaimCore.Tests.Fixtures.paid ()
            let original = Inputs.registration

            let registration =
                (ClaimCore.TestSupport.Inputs.registrationWith
                    [
                        BusinessField.IncidentDate, ("2026-10-01")
                        BusinessField.IncidentNotificationDate, ("2026-10-02")
                        BusinessField.ClaimantName, (String('A', 201))
                        BusinessField.InsurerName, (String('B', 201))
                    ]
                    (original))

            let correction: AuthoredCaseAct =
                CaseAct.Correct
                    {
                        Registration = Amendment.Update(Inputs.registrationUpdate registration)
                        Decision =
                            Change.Update(
                                Inputs.decisionUpdate (
                                    ClaimCore.TestSupport.Inputs.decisionFrom
                                        [
                                            BusinessField.PaymentDecisionDate, ("2026-10-01")
                                            BusinessField.PayableAmount, ("750")
                                            BusinessField.PayableCurrency, ("EUR")
                                        ]
                                )
                            )
                        Payment =
                            PaymentChange.Replace(
                                ClaimCore.TestSupport.Inputs.paymentFrom
                                    [ BusinessField.PaymentDate, ("2026-09-30") ]
                            )
                    }

            let request =
                ClaimCore.Tests.Fixtures.request
                    (Revision.value (Case.snapshot before).Revision)
                    correction

            let result = CaseFixtures.decide (DateOnly(2026, 7, 1)) request (Some before)
            let actual = result |> targets

            let expected =
                [
                    BusinessField.IncidentDate
                    BusinessField.IncidentNotificationDate
                    BusinessField.ClaimantName
                    BusinessField.InsurerName
                    BusinessField.PaymentDecisionDate
                    BusinessField.PaymentDecisionDate
                    BusinessField.PaymentDate
                    BusinessField.PaymentDate
                ]
                |> List.map InputTarget.Field

            Expect.equal
                actual
                expected
                "Chronological/future issues remain distinct but stable by their authored input"

            Expect.isLessThanOrEqual
                actual.Length
                ClaimCore.Protocol.DiagnosticCodecs.MaximumInputIssues
                "The worst phase-two field checks fit the bounded issue envelope"

            let inputs =
                ClaimCore.Protocol.OperationCatalog.derive (
                    ClaimCore.Protocol.CatalogOperation.Act(
                        ClaimCore.Protocol.ActKind.Case CaseActKind.Correct
                    )
                )
                |> fun bindings -> bindings.Inputs

            let ranks =
                actual
                |> List.map (fun target ->
                    let token = InputTarget.token target

                    inputs
                    |> List.findIndex (fun input ->
                        List.contains token input.Binding.DiagnosticTargets))

            Expect.equal
                ranks
                (List.sort ranks)
                "Core declaration order agrees with codec-derived catalog order")

let private opening reference replacements =
    {
        OperationId = Guid.Parse "70000000-0000-4000-8000-000000000002"
        CaseReference = reference
        ExpectedRevision = 0L
        Act = CaseAct.Open(Inputs.registrationFrom replacements)
    }

let private issues =
    function
    | Error(Refusal.InvalidInput input) -> InputIssues.toList input
    | result -> failtestf "Expected accumulated input issues, got %A" result

[<Tests>]
let oneRound =
    testList
        "one-round authored admission"
        [
            testCase "M12 reports all six failures in the first refusal" (fun () ->
                let input =
                    opening
                        "WEB 0001"
                        [
                            BusinessField.IncidentDate, "2026-11-05"
                            BusinessField.IncidentNotificationDate, "2026-11-03"
                            BusinessField.IncidentCountry, "latvia"
                            BusinessField.ClaimantName, "  Padded Name  "
                            BusinessField.ClaimedCurrency, "eur"
                        ]

                let actual = Case.admit (DateOnly(2026, 11, 4)) None input |> issues

                Expect.equal
                    (actual |> List.map (fun issue -> issue.Target, issue.Violation))
                    [
                        InputTarget.Field BusinessField.CaseReference, Violation.ReferenceFormat
                        InputTarget.Field BusinessField.IncidentDate, Violation.FutureDate
                        InputTarget.Field BusinessField.IncidentNotificationDate,
                        Violation.ChronologicalOrder
                        InputTarget.Field BusinessField.IncidentCountry, Violation.CountryFormat
                        InputTarget.Field BusinessField.ClaimantName,
                        Violation.SurroundingWhitespace
                        InputTarget.Field BusinessField.ClaimedCurrency, Violation.CurrencyFormat
                    ]
                    "Syntax failures cannot hide admission or chronology failures"

                Expect.isSome actual[2].RelatedDate "Chronology identifies its counterpart"
                let staged = Case.parse input |> issues

                Expect.isFalse
                    (staged = actual)
                    "Permanent parsing is the rejecting control for staged admission")
            testCase "random mixed syntax and admission violations accumulate" (fun () ->
                run
                    "mixed admission subsets"
                    (property {
                        let! mask = Gen.int32 (Range.constant 0 63)
                        let invalid bit = mask &&& (1 <<< bit) <> 0
                        let pick bit bad good = if invalid bit then bad else good

                        let input =
                            opening
                                (pick 0 "BAD REF" "VALID-REF")
                                [
                                    BusinessField.IncidentDate, "2026-11-03"
                                    BusinessField.IncidentNotificationDate,
                                    pick 1 "2026-11-05" "2026-11-04"
                                    BusinessField.IncidentCountry, pick 2 "latvia" "LV"
                                    BusinessField.ClaimantName, pick 3 " Padded " "Claimant"
                                    BusinessField.ClaimedAmount, pick 4 "1,234" "1234"
                                    BusinessField.ClaimedCurrency, pick 5 "eur" "EUR"
                                ]

                        let actual = Case.admit (DateOnly(2026, 11, 4)) None input |> targets

                        let expected =
                            [
                                InputTarget.Field BusinessField.CaseReference
                                InputTarget.Field BusinessField.IncidentNotificationDate
                                InputTarget.Field BusinessField.IncidentCountry
                                InputTarget.Field BusinessField.ClaimantName
                                InputTarget.Field BusinessField.ClaimedAmount
                                InputTarget.Field BusinessField.ClaimedCurrency
                            ]
                            |> List.indexed
                            |> List.choose (fun (bit, target) ->
                                if invalid bit then Some target else None)

                        return actual = expected
                    }))
            testCase "complete reminder metadata stays supplied in every changing act" (fun () ->
                let plan =
                    {
                        ReminderPlanInput.Action = "Valid action"
                        Due = "2026-11-04"
                    }

                let changing =
                    [
                        ReminderAct.Update(Guid.Empty, -1L, plan)
                        ReminderAct.Complete(Guid.Empty, -1L)
                        ReminderAct.Cancel(Guid.Empty, -1L)
                        ReminderAct.Reopen(Guid.Empty, -1L, "2026-11-04")
                        ReminderAct.CompleteAndPlanNext(Guid.Empty, -1L, plan)
                    ]

                for act in changing do
                    let issues =
                        Validation.syntax (AuthoredInput.Reminder(Guid.Empty, act))
                        |> Option.get
                        |> InputIssues.toList
                        |> List.map (fun issue -> issue.Target, issue.Violation)

                    Expect.equal
                        issues
                        [
                            InputTarget.OperationId, Violation.OperationIdRequired
                            InputTarget.ReminderId, Violation.ReminderIdRequired
                            InputTarget.ExpectedReminderRevision, Violation.ExpectedRevisionRange
                        ]
                        "Invalid supplied IDs and revision are retained, ordered and not treated as omitted intent"

                let creation =
                    Validation.syntax (AuthoredInput.Reminder(Guid.Empty, ReminderAct.Create plan))
                    |> Option.get
                    |> InputIssues.toList
                    |> List.map (fun issue -> issue.Target, issue.Violation)

                Expect.equal
                    creation
                    [ InputTarget.OperationId, Violation.OperationIdRequired ]
                    "Creation has no invented prior reminder ID or revision")
            testCase "offline syntax admits future dates but checks text and format" (fun () ->
                let input =
                    opening
                        "VALID-REF"
                        [
                            BusinessField.IncidentDate, "2026-11-03"
                            BusinessField.IncidentNotificationDate, "9999-12-31"
                            BusinessField.IncidentCountry, "latvia"
                            BusinessField.ClaimantName, " Padded "
                        ]

                let actual =
                    Validation.syntax (
                        AuthoredInput.Case(
                            input.OperationId,
                            Some input.CaseReference,
                            input.ExpectedRevision,
                            input.Act
                        )
                    )
                    |> Option.get
                    |> InputIssues.toList

                Expect.equal
                    (actual |> List.map _.Violation)
                    [ Violation.CountryFormat; Violation.SurroundingWhitespace ]
                    "Offline syntax has no business-date admission")
        ]
