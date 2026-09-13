module ClaimCore.Tests.DomainTests

open Expecto
open ClaimCore.Domain
open ClaimCore.Tests.Fixtures

let private registrationTests =
    testList
        "registration facts"
        [
            testCase
                "registration preserves the requested meanings, without invented later facts"
                (fun () ->
                    let view = opened () |> Claim.view
                    Expect.equal view.Version 1L "Technical first revision"
                    Expect.equal view.Fields.Status CaseStatus.Opened "Only requested status"

                    Expect.isTrue
                        (view.Fields.IncidentNotificationDate = "2026-08-03")
                        "This handler's notification date"

                    Expect.isTrue
                        (view.Fields.ClaimantName = registration.ClaimantName)
                        "A company name is not split"

                    Expect.isTrue
                        (view.Fields.InsurerName = registration.InsurerName)
                        "No liability inference"

                    Expect.isNone view.Fields.PaymentDecisionDate "No invented decision date"
                    Expect.isNone view.Fields.PayableAmount "No invented payable amount"
                    Expect.isNone view.Fields.PayableCurrency "No invented payable currency"
                    Expect.isNone view.Fields.PaymentDate "No invented payment")
            testCase "notification before incident is rejected" (fun () ->
                let facts =
                    { registration with
                        IncidentNotificationDate = "2026-07-31"
                    }

                Expect.isTrue
                    (Claim.decide today (request 0L (Command.Open facts)) None |> isInvalid)
                    "Chronology")
            testCase "future incident and notification are rejected" (fun () ->
                let facts =
                    { registration with
                        IncidentDate = "2027-01-01"
                        IncidentNotificationDate = "2027-01-02"
                    }

                Expect.isTrue
                    (Claim.decide today (request 0L (Command.Open facts)) None |> isInvalid)
                    "Future")
        ]

let private lifecycleTests =
    testList
        "lifecycle meaning"
        [
            testCase "decision is a tuple but is not a payment" (fun () ->
                let fields = (decided () |> Claim.view).Fields
                Expect.isTrue (fields.PaymentDecisionDate = Some "2026-08-15") "Decided on"
                Expect.isTrue (fields.PayableAmount = Some "750") "Exact decimal"
                Expect.isTrue (fields.PayableCurrency = Some "EUR") "Decided currency"
                Expect.isNone fields.PaymentDate "Still unpaid"
                Expect.equal fields.Status CaseStatus.Opened "Still open")
            testCase "payment is not closure" (fun () ->
                let fields = (paid () |> Claim.view).Fields
                Expect.isTrue (fields.PaymentDate = Some "2026-08-20") "Recorded date"
                Expect.equal fields.Status CaseStatus.Opened "No automatic closure")
            testCase "unpaid and undecided case can close" (fun () ->
                let fields = (opened () |> apply Command.Close |> accepted |> Claim.view).Fields
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
                    |> apply Command.Close
                    |> accepted
                    |> apply Command.Reopen
                    |> accepted
                    |> Claim.view

                Expect.isTrue
                    (after.Fields = (Claim.view original).Fields)
                    "Only the two explicit status transitions"

                Expect.equal
                    after.Version
                    ((Claim.view original).Version + 2L)
                    "Technical revisions")
            testCase "stale version cannot overwrite a case" (fun () ->
                let result = Claim.decide today (request 0L Command.Close) (Some(opened ()))

                Expect.isTrue
                    ((result |> Result.map Claim.view) = Error(DomainError.VersionConflict 1L))
                    "No lost update")
            testCase "missing case rejected" (fun () ->
                let result =
                    Claim.decide today (request 1L Command.Close) None |> Result.map Claim.view

                Expect.isTrue (result = Error DomainError.NotFound) "Not found")
        ]

let private paymentGuardTests =
    testList
        "payment guards"
        [
            testCase "payment requires decision" (fun () ->
                let result =
                    opened ()
                    |> apply (Command.RecordPayment "2026-08-20")
                    |> Result.map Claim.view

                Expect.isTrue (result = Error DomainError.DecisionRequired) "Decision required")
            testCase "second full payment rejected" (fun () ->
                let result =
                    paid () |> apply (Command.RecordPayment "2026-08-21") |> Result.map Claim.view

                Expect.isTrue
                    (result = Error DomainError.PaymentAlreadyRecorded)
                    "No instalment model")
            testCase "zero decision allowed but no fictitious zero transfer" (fun () ->
                let claim =
                    opened ()
                    |> apply (Command.Decide { decision with PayableAmount = "0" })
                    |> accepted

                let result =
                    apply (Command.RecordPayment "2026-08-20") claim |> Result.map Claim.view

                Expect.isTrue (result = Error DomainError.ZeroDecisionCannotBePaid) "No transfer"

                Expect.isFalse
                    (Claim.availableCommands claim |> List.contains "RECORD_PAYMENT")
                    "Advisory actions agree")
            testCase "payable can exceed claim and have a different currency" (fun () ->
                let input =
                    { decision with
                        PayableAmount = "2500.01"
                        PayableCurrency = "USD"
                    }

                let fields =
                    (opened () |> apply (Command.Decide input) |> accepted |> Claim.view).Fields

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
                let result = paid () |> apply (Command.Decide decision) |> Result.map Claim.view

                Expect.isTrue
                    (result = Error DomainError.DecisionAlreadyPaid)
                    "Explicit correction first")
            testCase "unpaid decision can be replaced" (fun () ->
                let fields =
                    (decided ()
                     |> apply (Command.Decide { decision with PayableAmount = "500" })
                     |> accepted
                     |> Claim.view)
                        .Fields

                Expect.isTrue (fields.PayableAmount = Some "500") "Explicit replacement")
            testCase "registration amendment requires withdrawal of an existing decision" (fun () ->
                let amendment =
                    decided ()
                    |> apply (Command.AmendRegistration registration)
                    |> Result.map Claim.view

                Expect.isTrue
                    (amendment = Error DomainError.AmendmentRequiresUndecided)
                    "No stale decision"

                let fields =
                    (decided ()
                     |> apply Command.WithdrawDecision
                     |> accepted
                     |> apply (Command.AmendRegistration registration)
                     |> accepted
                     |> Claim.view)
                        .Fields

                Expect.isNone fields.PaymentDecisionDate "Decision withdrawn")
        ]

let private correctionTests =
    testList
        "correction state"
        [
            testCase "clear payment requires no extra claims-entry data" (fun () ->
                let before = paid ()
                let after = before |> apply Command.ClearPayment |> accepted |> Claim.view

                let expected =
                    { (Claim.view before).Fields with
                        PaymentDate = None
                    }

                Expect.isTrue (after.Fields = expected) "Only requested payment date cleared"

                Expect.equal after.Version 4L "History has a new technical revision")
            testCase "clear payment without a recorded date is rejected" (fun () ->
                let result = decided () |> apply Command.ClearPayment |> Result.map Claim.view

                Expect.isTrue (result = Error DomainError.PaymentNotRecorded) "No payment")
            testCase "closed cases must reopen before edits" (fun () ->
                let claim = decided () |> apply Command.Close |> accepted

                let result =
                    apply (Command.RecordPayment "2026-08-20") claim |> Result.map Claim.view

                Expect.isTrue (result = Error DomainError.ClosedCase) "Explicit reopen")
        ]

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
