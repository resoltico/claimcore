module ClaimCore.Tests.BusinessDateTests

open System
open Expecto
open ClaimCore.Domain
open ClaimCore.Tests.Fixtures

let private applyAt date command claim =
    Claim.decide date (request (Claim.view claim).Version command) (Some claim)

let private future field result =
    Expect.equal
        (result |> Result.map (fun _ -> ()))
        (Error(DomainError.InvalidInput(field, InputViolation.Date DateViolation.FutureDate)))
        "A newly asserted date is bounded by business time"

let private amendmentRetainsDates =
    testCase "[CC-DOM-002] amendment retains accepted dates after clock rollback" (fun () ->
        let before = opened ()

        let changed =
            { registration with
                ClaimantName = "Synthetic amended party"
            }

        let after =
            applyAt (DateOnly(2026, 7, 31)) (Command.AmendRegistration changed) before
            |> accepted
            |> Claim.view

        Expect.equal after.Version 2L "One revision"

        Expect.isTrue
            (after.Fields.IncidentNotificationDate = registration.IncidentNotificationDate)
            "Accepted historical date retained")

let private newIncidentIsBounded =
    testCase "[CC-DOM-002] an unchanged notification cannot hide a new future incident" (fun () ->
        let changed =
            { registration with
                IncidentDate = "2026-08-02"
            }

        opened ()
        |> applyAt (DateOnly(2026, 7, 31)) (Command.AmendRegistration changed)
        |> future InputTarget.IncidentDate)

let private newNotificationIsBounded =
    testCase "[CC-DOM-002] newly replaced notification dates are checked independently" (fun () ->
        let changed =
            { registration with
                IncidentNotificationDate = "2026-08-04"
            }

        opened ()
        |> applyAt (DateOnly(2026, 8, 3)) (Command.AmendRegistration changed)
        |> future InputTarget.IncidentNotificationDate)

let private decisionRetainsDate =
    testCase
        "[CC-DOM-002] decision amount replacement retains its accepted date after rollback"
        (fun () ->
            let changed = { decision with PayableAmount = "650" }

            let after =
                decided ()
                |> applyAt (DateOnly(2026, 8, 10)) (Command.Decide changed)
                |> accepted
                |> Claim.view

            Expect.isTrue (after.Fields.PayableAmount = Some "650") "Amount replaced"

            Expect.isTrue
                (after.Fields.PaymentDecisionDate = Some decision.PaymentDecisionDate)
                "Accepted decision date retained")

let private newDecisionDateIsBounded =
    testCase "[CC-DOM-002] changing an accepted decision date asserts a new date" (fun () ->
        let changed =
            { decision with
                PaymentDecisionDate = "2026-08-14"
            }

        decided ()
        |> applyAt (DateOnly(2026, 8, 10)) (Command.Decide changed)
        |> future InputTarget.PaymentDecisionDate)

let private initialDecisionIsBounded =
    testCase "[CC-DOM-002] initial decisions cannot reuse a future historical date" (fun () ->
        opened ()
        |> applyAt (DateOnly(2026, 8, 10)) (Command.Decide decision)
        |> future InputTarget.PaymentDecisionDate)

let private withdrawnDateIsAbsent =
    testCase
        "[CC-DOM-002] withdrawing a decision removes its date reaffirmation authority"
        (fun () ->
            decided ()
            |> apply Command.WithdrawDecision
            |> accepted
            |> applyAt (DateOnly(2026, 8, 10)) (Command.Decide decision)
            |> future InputTarget.PaymentDecisionDate)

let private clearedPaymentDateIsAbsent =
    testCase "[CC-DOM-002] clearing payment removes its date reaffirmation authority" (fun () ->
        paid ()
        |> apply Command.ClearPayment
        |> accepted
        |> applyAt (DateOnly(2026, 8, 16)) (Command.RecordPayment "2026-08-20")
        |> future InputTarget.PaymentDate)

let private closedCorrectionRetainsDates =
    testCase
        "[CC-DOM-002] a closed paid correction reaffirms accepted dates after rollback"
        (fun () ->
            let before = paid () |> apply Command.Close |> accepted

            let command =
                Command.CorrectCase
                    {
                        Registration =
                            RegistrationCorrection.Replace
                                { registration with
                                    ClaimantName = "Synthetic corrected party"
                                }
                        Decision = DecisionCorrection.Replace decision
                        Payment = PaymentCorrection.Replace "2026-08-20"
                    }

            let after =
                before |> applyAt (DateOnly(2026, 7, 31)) command |> accepted |> Claim.view

            Expect.equal after.Fields.Status CaseStatus.Closed "Correction retains closure"

            Expect.isTrue
                (after.Fields.PaymentDate = Some "2026-08-20")
                "Reaffirmed accepted payment"

            Expect.equal after.Version 5L "One complete revision")

let private correctionIncidentIsBounded =
    testCase
        "[CC-DOM-002] a correction cannot hide a new future incident behind accepted dates"
        (fun () ->
            let command =
                Command.CorrectCase
                    {
                        Registration =
                            RegistrationCorrection.Replace
                                { registration with
                                    IncidentDate = "2026-08-02"
                                }
                        Decision = DecisionCorrection.Keep
                        Payment = PaymentCorrection.Keep
                    }

            paid ()
            |> applyAt (DateOnly(2026, 7, 31)) command
            |> future InputTarget.IncidentDate)

let private spellingDoesNotRevise =
    testCase
        "[CC-DOM-002] equivalent amount spelling remains a correction no-op after rollback"
        (fun () ->
            let command =
                Command.CorrectCase
                    {
                        Registration = RegistrationCorrection.Keep
                        Decision = DecisionCorrection.Replace decision
                        Payment = PaymentCorrection.Replace "2026-08-20"
                    }

            let result = paid () |> applyAt (DateOnly(2026, 7, 31)) command

            Expect.equal result (Error DomainError.CorrectionNoChanges) "No spelling-only revision")

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

let tests = testList "business date assertions" [ ordinary; corrections ]
