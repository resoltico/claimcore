module ClaimCore.Tests.DomainTests

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
                        ((viewFields view).IncidentNotificationDate = "2026-08-03")
                        "This handler's notification date"

                    Expect.isTrue
                        ((viewFields view).ClaimantName = registration.ClaimantName)
                        "A company name is not split"

                    Expect.isTrue
                        ((viewFields view).InsurerName = registration.InsurerName)
                        "No liability inference"

                    Expect.isNone (viewFields view).PaymentDecisionDate "No invented decision date"
                    Expect.isNone (viewFields view).PayableAmount "No invented payable amount"
                    Expect.isNone (viewFields view).PayableCurrency "No invented payable currency"
                    Expect.isNone (viewFields view).PaymentDate "No invented payment")
            testCase "notification before incident is rejected" (fun () ->
                let facts =
                    { registration with
                        IncidentNotificationDate = "2026-07-31"
                    }

                Expect.isTrue
                    (Case.decide today (request 0L (BusinessCommand.Open facts)) None |> isInvalid)
                    "Chronology")
            testCase "future incident and notification are rejected" (fun () ->
                let facts =
                    { registration with
                        IncidentDate = "2027-01-01"
                        IncidentNotificationDate = "2027-01-02"
                    }

                Expect.isTrue
                    (Case.decide today (request 0L (BusinessCommand.Open facts)) None |> isInvalid)
                    "Future")
        ]

let private lifecycleTests =
    testList
        "lifecycle meaning"
        [
            testCase "decision is a tuple but is not a payment" (fun () ->
                let fields = (decided () |> Case.snapshot |> viewFields)
                Expect.isTrue (fields.PaymentDecisionDate = Some "2026-08-15") "Decided on"
                Expect.isTrue (fields.PayableAmount = Some "750") "Exact decimal"
                Expect.isTrue (fields.PayableCurrency = Some "EUR") "Decided currency"
                Expect.isNone fields.PaymentDate "Still unpaid"
                Expect.equal fields.Status CaseStatus.Opened "Still open")
            testCase "payment is not closure" (fun () ->
                let fields = (paid () |> Case.snapshot |> viewFields)
                Expect.isTrue (fields.PaymentDate = Some "2026-08-20") "Recorded date"
                Expect.equal fields.Status CaseStatus.Opened "No automatic closure")
            testCase "unpaid and undecided case can close" (fun () ->
                let fields =
                    (opened ()
                     |> apply BusinessCommand.Close
                     |> accepted
                     |> Case.snapshot
                     |> viewFields)

                Expect.equal fields.Status CaseStatus.Closed "Administrative closure"
                Expect.isNone fields.PaymentDecisionDate "No inferred decision"
                Expect.isNone fields.PaymentDate "No inferred payment")
        ]

let private identityAndVersionTests =
    testList
        "identity and revisions"
        [
            testCase "close and reopen preserve all business fields" (fun () ->
                let original = paid ()

                let after =
                    original
                    |> apply BusinessCommand.Close
                    |> accepted
                    |> apply BusinessCommand.Reopen
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
                let result = Case.decide today (request 0L BusinessCommand.Close) (Some(opened ()))

                Expect.isTrue
                    ((result |> Result.map Case.snapshot) = Error(CaseRefusal.RevisionConflict 1L))
                    "No lost update")
            testCase "missing case rejected" (fun () ->
                let result =
                    Case.decide today (request 1L BusinessCommand.Close) None
                    |> Result.map Case.snapshot

                Expect.isTrue (result = Error CaseRefusal.NotFound) "Not found")
        ]

let private paymentGuardTests =
    testList
        "payment guards"
        [
            testCase "payment requires decision" (fun () ->
                let result =
                    opened ()
                    |> apply (BusinessCommand.RecordPayment "2026-08-20")
                    |> Result.map Case.snapshot

                Expect.isTrue (result = Error CaseRefusal.DecisionRequired) "Decision required")
            testCase "second full payment rejected" (fun () ->
                let result =
                    paid ()
                    |> apply (BusinessCommand.RecordPayment "2026-08-21")
                    |> Result.map Case.snapshot

                Expect.isTrue
                    (result = Error CaseRefusal.PaymentAlreadyRecorded)
                    "No instalment model")
            testCase "zero decision allowed but no fictitious zero transfer" (fun () ->
                let claim =
                    opened ()
                    |> apply (BusinessCommand.Decide { decision with PayableAmount = "0" })
                    |> accepted

                let result =
                    apply (BusinessCommand.RecordPayment "2026-08-20") claim
                    |> Result.map Case.snapshot

                Expect.isTrue (result = Error CaseRefusal.ZeroDecisionCannotBePaid) "No transfer"

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
                     |> apply (BusinessCommand.Decide input)
                     |> accepted
                     |> Case.snapshot
                     |> viewFields)

                Expect.isTrue (fields.PayableAmount = Some "2500.01") "No invented coverage cap"

                Expect.isTrue
                    (fields.PayableCurrency = Some "USD")
                    "No invented currency conversion")
        ]

let private decisionEditingTests =
    testList
        "decision editing"
        [
            testCase "paid decision cannot be edited" (fun () ->
                let result =
                    paid () |> apply (BusinessCommand.Decide decision) |> Result.map Case.snapshot

                Expect.isTrue
                    (result = Error CaseRefusal.DecisionAlreadyPaid)
                    "Explicit correction first")
            testCase "unpaid decision can be replaced" (fun () ->
                let fields =
                    (decided ()
                     |> apply (BusinessCommand.Decide { decision with PayableAmount = "500" })
                     |> accepted
                     |> Case.snapshot
                     |> viewFields)

                Expect.isTrue (fields.PayableAmount = Some "500") "Explicit replacement")
            testCase "registration amendment requires withdrawal of an existing decision" (fun () ->
                let amendment =
                    decided ()
                    |> apply (BusinessCommand.AmendRegistration registration)
                    |> Result.map Case.snapshot

                Expect.isTrue
                    (amendment = Error CaseRefusal.AmendmentRequiresUndecided)
                    "No stale decision"

                let fields =
                    (decided ()
                     |> apply BusinessCommand.WithdrawDecision
                     |> accepted
                     |> apply (BusinessCommand.AmendRegistration registration)
                     |> accepted
                     |> Case.snapshot
                     |> viewFields)

                Expect.isNone fields.PaymentDecisionDate "Decision withdrawn")
        ]

let private correctionTests =
    testList
        "correction state"
        [
            testCase "clear payment requires no extra claims-entry data" (fun () ->
                let before = paid ()

                let after =
                    before |> apply BusinessCommand.ClearPayment |> accepted |> Case.snapshot

                let expected =
                    { (Case.snapshot before |> viewFields) with
                        PaymentDate = None
                    }

                Expect.isTrue ((viewFields after) = expected) "Only requested payment date cleared"

                Expect.equal after.Revision 4L "History has a new technical revision")
            testCase "clear payment without a recorded date is rejected" (fun () ->
                let result =
                    decided () |> apply BusinessCommand.ClearPayment |> Result.map Case.snapshot

                Expect.isTrue (result = Error CaseRefusal.PaymentNotRecorded) "No payment")
            testCase "closed cases must reopen before edits" (fun () ->
                let claim = decided () |> apply BusinessCommand.Close |> accepted

                let result =
                    apply (BusinessCommand.RecordPayment "2026-08-20") claim
                    |> Result.map Case.snapshot

                Expect.isTrue (result = Error CaseRefusal.ClosedCase) "Explicit reopen")
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
