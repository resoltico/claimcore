module ClaimCore.Tests.BusinessDateTests

open ClaimCore.TestSupport

open ClaimCore.Contract

open System
open Expecto
open ClaimCore.Domain
open ClaimCore.Tests.Fixtures

let private applyAt date act claim =
    CaseFixtures.decide date (request (Case.snapshot claim).Revision act) (Some claim)

let private future field result =
    Expect.equal
        (result |> Result.map (fun _ -> ()))
        (Error(Refusal.InvalidInput(field, Violation.FutureDate)))
        "A newly asserted date is bounded by business time"

let private amendmentRetainsDates =
    testCase "amendment retains accepted dates after clock rollback" (fun () ->
        let before = opened ()

        let changed =
            { registration with
                ClaimantName = "Synthetic amended party"
            }

        let after =
            applyAt (DateOnly(2026, 7, 31)) (UnvalidatedCaseAct.AmendRegistration changed) before
            |> accepted
            |> Case.snapshot

        Expect.equal after.Revision 2L "One revision"

        Expect.isTrue
            (CalendarDate.format (viewFields after).Registration.IncidentNotificationDate =
                registration.IncidentNotificationDate)
            "Accepted historical date retained")

let private newIncidentIsBounded =
    testCase "an unchanged notification cannot hide a new future incident" (fun () ->
        let changed =
            { registration with
                IncidentDate = "2026-08-02"
            }

        opened ()
        |> applyAt (DateOnly(2026, 7, 31)) (UnvalidatedCaseAct.AmendRegistration changed)
        |> future (InputTarget.Field BusinessField.IncidentDate))

let private newNotificationIsBounded =
    testCase "newly replaced notification dates are checked independently" (fun () ->
        let changed =
            { registration with
                IncidentNotificationDate = "2026-08-04"
            }

        opened ()
        |> applyAt (DateOnly(2026, 8, 3)) (UnvalidatedCaseAct.AmendRegistration changed)
        |> future (InputTarget.Field BusinessField.IncidentNotificationDate))

let private decisionRetainsDate =
    testCase "decision amount replacement retains its accepted date after rollback" (fun () ->
        let changed = { decision with PayableAmount = "650" }

        let after =
            decided ()
            |> applyAt (DateOnly(2026, 8, 10)) (UnvalidatedCaseAct.Decide changed)
            |> accepted
            |> Case.snapshot

        Expect.isTrue
            (((viewFields after).Decision
              |> Option.map (fun value -> Money.format value.PayableAmount))
                =
                Some "650")
            "Amount replaced"

        Expect.isTrue
            (((viewFields after).Decision
              |> Option.map (fun value -> CalendarDate.format value.PaymentDecisionDate))
                =
                Some decision.PaymentDecisionDate)
            "Accepted decision date retained")

let private newDecisionDateIsBounded =
    testCase "changing an accepted decision date asserts a new date" (fun () ->
        let changed =
            { decision with
                PaymentDecisionDate = "2026-08-14"
            }

        decided ()
        |> applyAt (DateOnly(2026, 8, 10)) (UnvalidatedCaseAct.Decide changed)
        |> future (InputTarget.Field BusinessField.PaymentDecisionDate))

let private initialDecisionIsBounded =
    testCase "initial decisions cannot reuse a future historical date" (fun () ->
        opened ()
        |> applyAt (DateOnly(2026, 8, 10)) (UnvalidatedCaseAct.Decide decision)
        |> future (InputTarget.Field BusinessField.PaymentDecisionDate))

let private withdrawnDateIsAbsent =
    testCase "withdrawing a decision removes its date reaffirmation authority" (fun () ->
        decided ()
        |> apply UnvalidatedCaseAct.WithdrawDecision
        |> accepted
        |> applyAt (DateOnly(2026, 8, 10)) (UnvalidatedCaseAct.Decide decision)
        |> future (InputTarget.Field BusinessField.PaymentDecisionDate))

let private clearedPaymentDateIsAbsent =
    testCase "clearing payment removes its date reaffirmation authority" (fun () ->
        paid ()
        |> apply UnvalidatedCaseAct.ClearPayment
        |> accepted
        |> applyAt
            (DateOnly(2026, 8, 16))
            (UnvalidatedCaseAct.RecordPayment { PaymentDate = "2026-08-20" })
        |> future (InputTarget.Field BusinessField.PaymentDate))

let private closedCorrectionRetainsDates =
    testCase "a closed paid correction reaffirms accepted dates after rollback" (fun () ->
        let before = paid () |> apply UnvalidatedCaseAct.Close |> accepted

        let act =
            UnvalidatedCaseAct.Correct
                {
                    Registration =
                        RegistrationGroup.Replace
                            { registration with
                                ClaimantName = "Synthetic corrected party"
                            }
                    Decision = DecisionGroup.Replace decision
                    Payment = PaymentGroup.Replace { PaymentDate = "2026-08-20" }
                }

        let after =
            before |> applyAt (DateOnly(2026, 7, 31)) act |> accepted |> Case.snapshot

        Expect.equal (viewFields after).Status CaseStatus.Closed "Correction retains closure"

        Expect.isTrue
            (((viewFields after).Payment
              |> Option.map (fun value -> CalendarDate.format value.PaymentDate))
                =
                Some "2026-08-20")
            "Reaffirmed accepted payment"

        Expect.equal after.Revision 5L "One complete revision")

let private correctionIncidentIsBounded =
    testCase "a correction cannot hide a new future incident behind accepted dates" (fun () ->
        let act =
            UnvalidatedCaseAct.Correct
                {
                    Registration =
                        RegistrationGroup.Replace
                            { registration with
                                IncidentDate = "2026-08-02"
                            }
                    Decision = DecisionGroup.Keep
                    Payment = PaymentGroup.Keep
                }

        paid ()
        |> applyAt (DateOnly(2026, 7, 31)) act
        |> future (InputTarget.Field BusinessField.IncidentDate))

let private spellingDoesNotRevise =
    testCase "equivalent amount spelling remains a correction no-op after rollback" (fun () ->
        let act =
            UnvalidatedCaseAct.Correct
                {
                    Registration = RegistrationGroup.Keep
                    Decision = DecisionGroup.Replace decision
                    Payment = PaymentGroup.Replace { PaymentDate = "2026-08-20" }
                }

        let result = paid () |> applyAt (DateOnly(2026, 7, 31)) act

        Expect.equal result (Error Refusal.CorrectionNoChanges) "No spelling-only revision")

let private ordinary =
    testList
        "ordinary assertions"
        [
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
            spellingDoesNotRevise
        ]

[<Tests>]
let tests = testList "business date assertions" [ ordinary; corrections ]
