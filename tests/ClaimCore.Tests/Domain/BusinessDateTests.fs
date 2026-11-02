module ClaimCore.Tests.BusinessDateTests

open ClaimCore.TestSupport

open ClaimCore.Protocol

open System
open Expecto
open ClaimCore.Domain
open ClaimCore.Tests.Fixtures

let private applyAt date act claim =
    CaseFixtures.decide
        date
        (request (Revision.value (Case.snapshot claim).Revision) act)
        (Some claim)

let private future field result =
    Expect.equal
        (result |> Result.map (fun _ -> ()))
        (Error(Refusal.invalidInput (field, Violation.FutureDate)))
        "A newly asserted date is bounded by business time"

let private openingAccumulatesAssertions =
    testCase
        "opening reports future dates and surrounding name whitespace in field order"
        (fun () ->
            let facts =
                (ClaimCore.TestSupport.Inputs.registrationWith
                    [ BusinessField.ClaimantName, (" Synthetic claimant ") ]
                    (registration))

            let outcome =
                CaseFixtures.decide
                    (DateOnly(2026, 7, 31))
                    (request 0L (AuthoredCaseAct.Open facts))
                    None

            match outcome with
            | Error(Refusal.InvalidInput issues) ->
                Expect.equal
                    (InputIssues.toList issues
                     |> List.map (fun issue -> issue.Target, issue.Violation))
                    [
                        InputTarget.Field BusinessField.IncidentDate, Violation.FutureDate
                        InputTarget.Field BusinessField.IncidentNotificationDate,
                        Violation.FutureDate
                        InputTarget.Field BusinessField.ClaimantName,
                        Violation.SurroundingWhitespace
                    ]
                    "Independent newly asserted values retain field order"
            | other -> failtestf "Expected accumulated assertions, got %A" other)

let private registrationPatch: RegistrationUpdateInput =
    {
        IncidentDate = None
        NotificationDate = None
        Country = None
        Claimant = None
        Insurer = None
        Claimed = None
    }

let private amendmentRetainsDates =
    testCase "partial amendment retains omitted accepted dates after clock rollback" (fun () ->
        let after =
            opened ()
            |> applyAt
                (DateOnly(2026, 7, 31))
                (CaseAct.AmendRegistration
                    { registrationPatch with
                        Claimant = Some "Synthetic amended party"
                    })
            |> accepted
            |> Case.snapshot

        Expect.equal (Revision.value after.Revision) 2L "One revision"

        Expect.equal
            (Registration.incidentNotificationDate (viewFields after).Registration
             |> CalendarDate.format)
            "2026-08-03"
            "Omitted date retained")

let private newIncidentIsBounded =
    testCase "an omitted notification cannot hide a newly asserted future incident" (fun () ->
        opened ()
        |> applyAt
            (DateOnly(2026, 7, 31))
            (CaseAct.AmendRegistration
                { registrationPatch with
                    IncidentDate = Some "2026-08-02"
                })
        |> future (InputTarget.Field BusinessField.IncidentDate))

let private newNotificationIsBounded =
    testCase "newly asserted notification is checked independently" (fun () ->
        opened ()
        |> applyAt
            (DateOnly(2026, 8, 3))
            (CaseAct.AmendRegistration
                { registrationPatch with
                    NotificationDate = Some "2026-08-04"
                })
        |> future (InputTarget.Field BusinessField.IncidentNotificationDate))

let private decisionRetainsDate =
    testCase "decision amount replacement retains its accepted date after rollback" (fun () ->
        let changed =
            (ClaimCore.TestSupport.Inputs.decisionWith
                [ BusinessField.PayableAmount, ("650") ]
                (decision))

        let after =
            decided ()
            |> applyAt (DateOnly(2026, 8, 10)) (AuthoredCaseAct.Decide changed)
            |> accepted
            |> Case.snapshot

        Expect.isTrue
            (((viewFields after).Decision
              |> Option.map (fun value -> Money.format (Decision.payable (value))))
                =
                Some "650")
            "Amount replaced"

        Expect.isTrue
            (((viewFields after).Decision
              |> Option.map (fun value -> CalendarDate.format (Decision.date (value))))
                =
                Some(DecisionInput.value BusinessField.PaymentDecisionDate (decision)))
            "Accepted decision date retained")

let private newDecisionDateIsBounded =
    testCase "changing an accepted decision date asserts a new date" (fun () ->
        let changed =
            (ClaimCore.TestSupport.Inputs.decisionWith
                [ BusinessField.PaymentDecisionDate, ("2026-08-14") ]
                (decision))

        decided ()
        |> applyAt (DateOnly(2026, 8, 10)) (AuthoredCaseAct.Decide changed)
        |> future (InputTarget.Field BusinessField.PaymentDecisionDate))

let private initialDecisionIsBounded =
    testCase "initial decisions cannot reuse a future historical date" (fun () ->
        opened ()
        |> applyAt (DateOnly(2026, 8, 10)) (AuthoredCaseAct.Decide decision)
        |> future (InputTarget.Field BusinessField.PaymentDecisionDate))

let private withdrawnDateIsAbsent =
    testCase "withdrawing a decision removes its date reaffirmation authority" (fun () ->
        decided ()
        |> apply (AuthoredCaseAct.Bare BareAct.WithdrawDecision)
        |> accepted
        |> applyAt (DateOnly(2026, 8, 10)) (AuthoredCaseAct.Decide decision)
        |> future (InputTarget.Field BusinessField.PaymentDecisionDate))

let private clearedPaymentDateIsAbsent =
    testCase "clearing payment removes its date reaffirmation authority" (fun () ->
        paid ()
        |> apply (AuthoredCaseAct.Bare BareAct.ClearPayment)
        |> accepted
        |> applyAt
            (DateOnly(2026, 8, 16))
            (AuthoredCaseAct.RecordPayment(
                ClaimCore.TestSupport.Inputs.paymentFrom
                    [ BusinessField.PaymentDate, ("2026-08-20") ]
            ))
        |> future (InputTarget.Field BusinessField.PaymentDate))

let private closedCorrectionRetainsDates =
    testCase "a closed paid correction reaffirms accepted dates after rollback" (fun () ->
        let before = paid () |> apply (AuthoredCaseAct.Bare BareAct.Close) |> accepted

        let act =
            AuthoredCaseAct.Correct
                {
                    Registration =
                        Amendment.Update
                            { registrationPatch with
                                Claimant = Some "Synthetic corrected party"
                            }
                    Decision =
                        Change.Update
                            {
                                Date = None
                                Payable =
                                    Some
                                        {
                                            MoneyInput.Amount = "650"
                                            Currency = "EUR"
                                        }
                            }
                    Payment = PaymentChange.Reaffirm
                }

        let after =
            before |> applyAt (DateOnly(2026, 7, 31)) act |> accepted |> Case.snapshot

        Expect.equal (viewFields after).Status CaseStatus.Closed "Correction retains closure"

        Expect.isTrue
            (((viewFields after).Payment
              |> Option.map (fun value -> CalendarDate.format (Payment.date (value))))
                =
                Some "2026-08-20")
            "Reaffirmed accepted payment"

        Expect.equal after.Revision (CaseFixtures.revision 5L) "One complete revision")

let private correctionIncidentIsBounded =
    testCase "a correction cannot hide a new future incident behind accepted dates" (fun () ->
        let act =
            AuthoredCaseAct.Correct
                {
                    Registration =
                        Amendment.Update
                            { registrationPatch with
                                IncidentDate = Some "2026-08-02"
                            }
                    Decision = Change.Keep
                    Payment = PaymentChange.Keep
                }

        paid ()
        |> applyAt (DateOnly(2026, 7, 31)) act
        |> future (InputTarget.Field BusinessField.IncidentDate))

let private incompleteCorrectionAccumulatesAssertions =
    testCase
        "incomplete paid corrections retain acknowledgement and independent assertion issues"
        (fun () ->
            let correction =
                AuthoredCaseAct.Correct
                    {
                        Registration =
                            Amendment.Update
                                { registrationPatch with
                                    IncidentDate = Some "2026-08-02"
                                    Claimant = Some " New claimant "
                                }
                        Decision = Change.Clear
                        Payment =
                            PaymentChange.Replace(
                                ClaimCore.TestSupport.Inputs.paymentFrom
                                    [ BusinessField.PaymentDate, ("2026-08-21") ]
                            )
                    }

            match paid () |> applyAt (DateOnly(2026, 7, 31)) correction with
            | Error(Refusal.InvalidInput issues) ->
                Expect.equal
                    (InputIssues.toList issues
                     |> List.map (fun issue -> issue.Target, issue.Violation))
                    [
                        InputTarget.Field BusinessField.IncidentDate, Violation.FutureDate
                        InputTarget.Field BusinessField.ClaimantName,
                        Violation.SurroundingWhitespace
                        InputTarget.Group RecordGroup.Payment, Violation.PaymentClearRequired
                        InputTarget.Field BusinessField.PaymentDate, Violation.FutureDate
                    ]
                    "An incomplete proposal neither hides acknowledgement nor drops new assertions"
            | other -> failtestf "Expected accumulated issues, got %A" other)

let private spellingDoesNotRevise =
    testCase "equivalent amount spelling remains a correction no-op after rollback" (fun () ->
        let act =
            AuthoredCaseAct.Correct
                {
                    Registration = Amendment.Keep
                    Decision =
                        Change.Update
                            {
                                Date = None
                                Payable =
                                    Some
                                        {
                                            MoneyInput.Amount = "750.0000"
                                            Currency = "EUR"
                                        }
                            }
                    Payment = PaymentChange.Reaffirm
                }

        let result = paid () |> applyAt (DateOnly(2026, 7, 31)) act

        Expect.equal
            result
            (Error(Refusal.Case CaseRefusal.CorrectionNoChanges))
            "No spelling-only revision")

let private ordinary =
    testList
        "ordinary assertions"
        [
            openingAccumulatesAssertions
            amendmentRetainsDates
            newIncidentIsBounded
            newNotificationIsBounded
            decisionRetainsDate
            newDecisionDateIsBounded
            initialDecisionIsBounded
            withdrawnDateIsAbsent
            clearedPaymentDateIsAbsent
        ]

let private corrections =
    testList
        "correction assertions"
        [
            closedCorrectionRetainsDates
            correctionIncidentIsBounded
            incompleteCorrectionAccumulatesAssertions
            spellingDoesNotRevise
        ]

[<Tests>]
let tests = testList "business date assertions" [ ordinary; corrections ]
