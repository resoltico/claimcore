module ClaimCore.Tests.BusinessDateTests

open ClaimCore.Contract

open System
open Expecto
open ClaimCore.Domain
open ClaimCore.Tests.Fixtures

let private applyAt date command claim =
    Case.decide date (request (Case.snapshot claim).Revision command) (Some claim)

let private future field result =
    Expect.equal
        (result |> Result.map (fun _ -> ()))
        (Error(Refusal.InvalidInput(field, Violation.FutureDate)))
        "A newly asserted date is bounded by business time"

let private amendmentRetainsDates =
    testCase "[CC-DOM-002] amendment retains accepted dates after clock rollback" (fun () ->
        let before = opened ()

        let changed =
            { registration with
                ClaimantName = "Synthetic amended party"
            }

        let after =
            applyAt (DateOnly(2026, 7, 31)) (CaseAct.AmendRegistration changed) before
            |> accepted
            |> Case.snapshot

        Expect.equal after.Revision 2L "One revision"

        Expect.isTrue
            ((viewFields after).IncidentNotificationDate = registration.IncidentNotificationDate)
            "Accepted historical date retained")

let private newIncidentIsBounded =
    testCase "[CC-DOM-002] an unchanged notification cannot hide a new future incident" (fun () ->
        let changed =
            { registration with
                IncidentDate = "2026-08-02"
            }

        opened ()
        |> applyAt (DateOnly(2026, 7, 31)) (CaseAct.AmendRegistration changed)
        |> future (InputTarget.Field BusinessField.IncidentDate))

let private newNotificationIsBounded =
    testCase "[CC-DOM-002] newly replaced notification dates are checked independently" (fun () ->
        let changed =
            { registration with
                IncidentNotificationDate = "2026-08-04"
            }

        opened ()
        |> applyAt (DateOnly(2026, 8, 3)) (CaseAct.AmendRegistration changed)
        |> future (InputTarget.Field BusinessField.IncidentNotificationDate))

let private decisionRetainsDate =
    testCase
        "[CC-DOM-002] decision amount replacement retains its accepted date after rollback"
        (fun () ->
            let changed = { decision with PayableAmount = "650" }

            let after =
                decided ()
                |> applyAt (DateOnly(2026, 8, 10)) (CaseAct.Decide changed)
                |> accepted
                |> Case.snapshot

            Expect.isTrue ((viewFields after).PayableAmount = Some "650") "Amount replaced"

            Expect.isTrue
                ((viewFields after).PaymentDecisionDate = Some decision.PaymentDecisionDate)
                "Accepted decision date retained")

let private newDecisionDateIsBounded =
    testCase "[CC-DOM-002] changing an accepted decision date asserts a new date" (fun () ->
        let changed =
            { decision with
                PaymentDecisionDate = "2026-08-14"
            }

        decided ()
        |> applyAt (DateOnly(2026, 8, 10)) (CaseAct.Decide changed)
        |> future (InputTarget.Field BusinessField.PaymentDecisionDate))

let private initialDecisionIsBounded =
    testCase "[CC-DOM-002] initial decisions cannot reuse a future historical date" (fun () ->
        opened ()
        |> applyAt (DateOnly(2026, 8, 10)) (CaseAct.Decide decision)
        |> future (InputTarget.Field BusinessField.PaymentDecisionDate))

let private withdrawnDateIsAbsent =
    testCase
        "[CC-DOM-002] withdrawing a decision removes its date reaffirmation authority"
        (fun () ->
            decided ()
            |> apply CaseAct.WithdrawDecision
            |> accepted
            |> applyAt (DateOnly(2026, 8, 10)) (CaseAct.Decide decision)
            |> future (InputTarget.Field BusinessField.PaymentDecisionDate))

let private clearedPaymentDateIsAbsent =
    testCase "[CC-DOM-002] clearing payment removes its date reaffirmation authority" (fun () ->
        paid ()
        |> apply CaseAct.ClearPayment
        |> accepted
        |> applyAt (DateOnly(2026, 8, 16)) (CaseAct.RecordPayment { PaymentDate = "2026-08-20" })
        |> future (InputTarget.Field BusinessField.PaymentDate))

let private closedCorrectionRetainsDates =
    testCase
        "[CC-DOM-002] a closed paid correction reaffirms accepted dates after rollback"
        (fun () ->
            let before = paid () |> apply CaseAct.Close |> accepted

            let command =
                CaseAct.Correct
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
                before |> applyAt (DateOnly(2026, 7, 31)) command |> accepted |> Case.snapshot

            Expect.equal (viewFields after).Status CaseStatus.Closed "Correction retains closure"

            Expect.isTrue
                ((viewFields after).PaymentDate = Some "2026-08-20")
                "Reaffirmed accepted payment"

            Expect.equal after.Revision 5L "One complete revision")

let private correctionIncidentIsBounded =
    testCase
        "[CC-DOM-002] a correction cannot hide a new future incident behind accepted dates"
        (fun () ->
            let command =
                CaseAct.Correct
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
            |> applyAt (DateOnly(2026, 7, 31)) command
            |> future (InputTarget.Field BusinessField.IncidentDate))

let private spellingDoesNotRevise =
    testCase
        "[CC-DOM-002] equivalent amount spelling remains a correction no-op after rollback"
        (fun () ->
            let command =
                CaseAct.Correct
                    {
                        Registration = RegistrationGroup.Keep
                        Decision = DecisionGroup.Replace decision
                        Payment = PaymentGroup.Replace { PaymentDate = "2026-08-20" }
                    }

            let result = paid () |> applyAt (DateOnly(2026, 7, 31)) command

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
