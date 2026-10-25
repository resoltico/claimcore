module ClaimCore.Tests.DomainTests

open ClaimCore.Contract

open Expecto
open ClaimCore.Domain
open ClaimCore.Tests.Fixtures

let private availableActs record =
    Case.eligibleActs record |> List.map CaseActKinds.token

let private registrationTests =
    testList
        "registration facts"
        [
            testCase
                "registration preserves the requested meanings, without invented later facts"
                (fun () ->
                    let view = opened () |> Case.snapshot
                    Expect.equal view.Revision 1L "Technical first revision"
                    Expect.equal (viewFields view).Status CaseStatus.Opened "Only requested status"

                    Expect.isTrue
                        ((viewFields view).Registration.IncidentNotificationDate = "2026-08-03")
                        "This handler's notification date"

                    Expect.isTrue
                        ((viewFields view).Registration.ClaimantName = registration.ClaimantName)
                        "A company name is not split"

                    Expect.isTrue
                        ((viewFields view).Registration.InsurerName = registration.InsurerName)
                        "No liability inference"

                    Expect.isNone
                        ((viewFields view).Decision |> Option.map _.PaymentDecisionDate)
                        "No invented decision date"

                    Expect.isNone
                        ((viewFields view).Decision |> Option.map _.PayableAmount)
                        "No invented payable amount"

                    Expect.isNone
                        ((viewFields view).Decision |> Option.map _.PayableCurrency)
                        "No invented payable currency"

                    Expect.isNone
                        ((viewFields view).Payment |> Option.map _.PaymentDate)
                        "No invented payment")
            testCase "notification before incident is rejected" (fun () ->
                let facts =
                    { registration with
                        IncidentNotificationDate = "2026-07-31"
                    }

                Expect.isTrue
                    (Case.decide today (request 0L (CaseAct.Open facts)) None |> isInvalid)
                    "Chronology")
            testCase "future incident and notification are rejected" (fun () ->
                let facts =
                    { registration with
                        IncidentDate = "2027-01-01"
                        IncidentNotificationDate = "2027-01-02"
                    }

                Expect.isTrue
                    (Case.decide today (request 0L (CaseAct.Open facts)) None |> isInvalid)
                    "Future")
        ]

let private lifecycleTests =
    testList
        "lifecycle meaning"
        [
            testCase "decision is a tuple but is not a payment" (fun () ->
                let fields = (decided () |> Case.snapshot |> viewFields)

                Expect.isTrue
                    ((fields.Decision |> Option.map _.PaymentDecisionDate) = Some "2026-08-15")
                    "Decided on"

                Expect.isTrue
                    ((fields.Decision |> Option.map _.PayableAmount) = Some "750")
                    "Exact decimal"

                Expect.isTrue
                    ((fields.Decision |> Option.map _.PayableCurrency) = Some "EUR")
                    "Decided currency"

                Expect.isNone (fields.Payment |> Option.map _.PaymentDate) "Still unpaid"
                Expect.equal fields.Status CaseStatus.Opened "Still open")
            testCase "payment is not closure" (fun () ->
                let fields = (paid () |> Case.snapshot |> viewFields)

                Expect.isTrue
                    ((fields.Payment |> Option.map _.PaymentDate) = Some "2026-08-20")
                    "Recorded date"

                Expect.equal fields.Status CaseStatus.Opened "No automatic closure")
            testCase "unpaid and undecided case can close" (fun () ->
                let fields =
                    (opened () |> apply CaseAct.Close |> accepted |> Case.snapshot |> viewFields)

                Expect.equal fields.Status CaseStatus.Closed "Administrative closure"

                Expect.isNone
                    (fields.Decision |> Option.map _.PaymentDecisionDate)
                    "No inferred decision"

                Expect.isNone (fields.Payment |> Option.map _.PaymentDate) "No inferred payment")
        ]

let private identityAndVersionTests =
    testList
        "identity and revisions"
        [
            testCase "close and reopen preserve all business fields" (fun () ->
                let original = paid ()

                let after =
                    original
                    |> apply CaseAct.Close
                    |> accepted
                    |> apply CaseAct.Reopen
                    |> accepted
                    |> Case.snapshot

                Expect.isTrue
                    ((viewFields after) = (Case.snapshot original |> viewFields))
                    "Only the two explicit status transitions"

                Expect.equal
                    after.Revision
                    ((Case.snapshot original).Revision + 2L)
                    "Technical revisions")
            testCase "stale version cannot overwrite a case" (fun () ->
                let result = Case.decide today (request 0L CaseAct.Close) (Some(opened ()))

                Expect.isTrue
                    ((result |> Result.map Case.snapshot) = Error(Refusal.RevisionConflict 1L))
                    "No lost update")
            testCase "missing case rejected" (fun () ->
                let result =
                    Case.decide today (request 1L CaseAct.Close) None |> Result.map Case.snapshot

                Expect.isTrue (result = Error Refusal.ResourceUnavailable) "Not found")
        ]

let private paymentGuardTests =
    testList
        "payment guards"
        [
            testCase "payment requires decision" (fun () ->
                let result =
                    opened ()
                    |> apply (CaseAct.RecordPayment { PaymentDate = "2026-08-20" })
                    |> Result.map Case.snapshot

                Expect.isTrue (result = Error Refusal.DecisionRequired) "Decision required")
            testCase "second full payment rejected" (fun () ->
                let result =
                    paid ()
                    |> apply (CaseAct.RecordPayment { PaymentDate = "2026-08-21" })
                    |> Result.map Case.snapshot

                Expect.isTrue (result = Error Refusal.PaymentAlreadyRecorded) "No instalment model")
            testCase "zero decision allowed but no fictitious zero transfer" (fun () ->
                let claim =
                    opened ()
                    |> apply (CaseAct.Decide { decision with PayableAmount = "0" })
                    |> accepted

                let result =
                    apply (CaseAct.RecordPayment { PaymentDate = "2026-08-20" }) claim
                    |> Result.map Case.snapshot

                Expect.isTrue (result = Error Refusal.ZeroDecisionCannotBePaid) "No transfer"

                Expect.isFalse
                    (availableActs claim |> List.contains "RECORD_PAYMENT")
                    "Advisory actions agree")
            testCase "payable can exceed claim and have a different currency" (fun () ->
                let input =
                    { decision with
                        PayableAmount = "2500.01"
                        PayableCurrency = "USD"
                    }

                let fields =
                    (opened ()
                     |> apply (CaseAct.Decide input)
                     |> accepted
                     |> Case.snapshot
                     |> viewFields)

                Expect.isTrue
                    ((fields.Decision |> Option.map _.PayableAmount) = Some "2500.01")
                    "No invented coverage cap"

                Expect.isTrue
                    ((fields.Decision |> Option.map _.PayableCurrency) = Some "USD")
                    "No invented currency conversion")
        ]

let private decisionEditingTests =
    testList
        "decision editing"
        [
            testCase "paid decision cannot be edited" (fun () ->
                let result = paid () |> apply (CaseAct.Decide decision) |> Result.map Case.snapshot

                Expect.isTrue
                    (result = Error Refusal.DecisionAlreadyPaid)
                    "Explicit correction first")
            testCase "unpaid decision can be replaced" (fun () ->
                let fields =
                    (decided ()
                     |> apply (CaseAct.Decide { decision with PayableAmount = "500" })
                     |> accepted
                     |> Case.snapshot
                     |> viewFields)

                Expect.isTrue
                    ((fields.Decision |> Option.map _.PayableAmount) = Some "500")
                    "Explicit replacement")
            testCase "registration amendment requires withdrawal of an existing decision" (fun () ->
                let amendment =
                    decided ()
                    |> apply (CaseAct.AmendRegistration registration)
                    |> Result.map Case.snapshot

                Expect.isTrue
                    (amendment = Error Refusal.AmendmentRequiresUndecided)
                    "No stale decision"

                let fields =
                    (decided ()
                     |> apply CaseAct.WithdrawDecision
                     |> accepted
                     |> apply (CaseAct.AmendRegistration registration)
                     |> accepted
                     |> Case.snapshot
                     |> viewFields)

                Expect.isNone
                    (fields.Decision |> Option.map _.PaymentDecisionDate)
                    "Decision withdrawn")
        ]

let private correctionTests =
    testList
        "correction state"
        [
            testCase "clear payment requires no extra claims-entry data" (fun () ->
                let before = paid ()

                let after = before |> apply CaseAct.ClearPayment |> accepted |> Case.snapshot

                let expected =
                    { (Case.snapshot before |> viewFields) with
                        Payment = None
                    }

                Expect.isTrue ((viewFields after) = expected) "Only requested payment date cleared"

                Expect.equal after.Revision 4L "History has a new technical revision")
            testCase "clear payment without a recorded date is rejected" (fun () ->
                let result = decided () |> apply CaseAct.ClearPayment |> Result.map Case.snapshot

                Expect.isTrue (result = Error Refusal.PaymentNotRecorded) "No payment")
            testCase "closed cases must reopen before edits" (fun () ->
                let claim = decided () |> apply CaseAct.Close |> accepted

                let result =
                    apply (CaseAct.RecordPayment { PaymentDate = "2026-08-20" }) claim
                    |> Result.map Case.snapshot

                Expect.isTrue (result = Error Refusal.CaseClosed) "Explicit reopen")
        ]

[<Tests>]
let tests =
    testList
        "domain"
        [
            registrationTests
            lifecycleTests
            identityAndVersionTests
            paymentGuardTests
            decisionEditingTests
            correctionTests
        ]
