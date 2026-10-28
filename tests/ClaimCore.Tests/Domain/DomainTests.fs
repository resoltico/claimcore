module ClaimCore.Tests.DomainTests

open ClaimCore.TestSupport

open ClaimCore.Protocol

open Expecto
open ClaimCore.Domain
open ClaimCore.Tests.Fixtures

let private availableActs record =
    Case.eligibleActs record |> List.map CaseActKind.token

let private registrationTests =
    testList
        "registration facts"
        [
            testCase
                "registration preserves the requested meanings, without invented later facts"
                (fun () ->
                    let view = opened () |> Case.snapshot

                    Expect.equal
                        view.Revision
                        (CaseFixtures.revision 1L)
                        "Technical first revision"

                    Expect.equal (viewFields view).Status CaseStatus.Opened "Only requested status"

                    Expect.isTrue
                        (CalendarDate.format (
                            Registration.incidentNotificationDate ((viewFields view).Registration)
                        )
                            =
                            "2026-08-03")
                        "This handler's notification date"

                    Expect.isTrue
                        (PartyName.format (
                            Registration.value
                                BusinessField.ClaimantName
                                ((viewFields view).Registration)
                            |> FieldValue.name
                        )
                            =
                            (RegistrationInput.value BusinessField.ClaimantName (registration)))
                        "A company name is not split"

                    Expect.isTrue
                        (PartyName.format (
                            Registration.value
                                BusinessField.InsurerName
                                ((viewFields view).Registration)
                            |> FieldValue.name
                        )
                            =
                            (RegistrationInput.value BusinessField.InsurerName (registration)))
                        "No liability inference"

                    Expect.isNone
                        ((viewFields view).Decision
                         |> Option.map (fun value -> CalendarDate.format (Decision.date (value))))
                        "No invented decision date"

                    Expect.isNone
                        ((viewFields view).Decision
                         |> Option.map (fun value -> Money.format (Decision.payable (value))))
                        "No invented payable amount"

                    Expect.isNone
                        ((viewFields view).Decision
                         |> Option.map (fun value -> Money.currency (Decision.payable (value))))
                        "No invented payable currency"

                    Expect.isNone
                        ((viewFields view).Payment
                         |> Option.map (fun value -> CalendarDate.format (Payment.date (value))))
                        "No invented payment")
            testCase "notification before incident is rejected" (fun () ->
                let facts =
                    (ClaimCore.TestSupport.Inputs.registrationWith
                        [ BusinessField.IncidentNotificationDate, ("2026-07-31") ]
                        (registration))

                Expect.isTrue
                    (CaseFixtures.decide today (request 0L (AuthoredCaseAct.Open facts)) None
                     |> isInvalid)
                    "Chronology")
            testCase "future incident and notification are rejected" (fun () ->
                let facts =
                    (ClaimCore.TestSupport.Inputs.registrationWith
                        [
                            BusinessField.IncidentDate, ("2027-01-01")
                            BusinessField.IncidentNotificationDate, ("2027-01-02")
                        ]
                        (registration))

                Expect.isTrue
                    (CaseFixtures.decide today (request 0L (AuthoredCaseAct.Open facts)) None
                     |> isInvalid)
                    "Future")
        ]

let private lifecycleTests =
    testList
        "lifecycle meaning"
        [
            testCase "decision is a tuple but is not a payment" (fun () ->
                let fields = (decided () |> Case.snapshot |> viewFields)

                Expect.isTrue
                    ((fields.Decision
                      |> Option.map (fun value -> CalendarDate.format (Decision.date (value))))
                        =
                        Some "2026-08-15")
                    "Decided on"

                Expect.isTrue
                    ((fields.Decision
                      |> Option.map (fun value -> Money.format (Decision.payable (value))))
                        =
                        Some "750")
                    "Exact decimal"

                Expect.isTrue
                    ((fields.Decision
                      |> Option.map (fun value -> Money.currency (Decision.payable (value))))
                        =
                        Some "EUR")
                    "Decided currency"

                Expect.isNone
                    (fields.Payment
                     |> Option.map (fun value -> CalendarDate.format (Payment.date (value))))
                    "Still unpaid"

                Expect.equal fields.Status CaseStatus.Opened "Still open")
            testCase "payment is not closure" (fun () ->
                let fields = (paid () |> Case.snapshot |> viewFields)

                Expect.isTrue
                    ((fields.Payment
                      |> Option.map (fun value -> CalendarDate.format (Payment.date (value))))
                        =
                        Some "2026-08-20")
                    "Recorded date"

                Expect.equal fields.Status CaseStatus.Opened "No automatic closure")
            testCase "unpaid and undecided case can close" (fun () ->
                let fields =
                    (opened ()
                     |> apply (AuthoredCaseAct.Bare BareAct.Close)
                     |> accepted
                     |> Case.snapshot
                     |> viewFields)

                Expect.equal fields.Status CaseStatus.Closed "Administrative closure"

                Expect.isNone
                    (fields.Decision
                     |> Option.map (fun value -> CalendarDate.format (Decision.date (value))))
                    "No inferred decision"

                Expect.isNone
                    (fields.Payment
                     |> Option.map (fun value -> CalendarDate.format (Payment.date (value))))
                    "No inferred payment")
        ]

let private identityAndVersionTests =
    testList
        "identity and revisions"
        [
            testCase "close and reopen preserve all business fields" (fun () ->
                let original = paid ()

                let after =
                    original
                    |> apply (AuthoredCaseAct.Bare BareAct.Close)
                    |> accepted
                    |> apply (AuthoredCaseAct.Bare BareAct.Reopen)
                    |> accepted
                    |> Case.snapshot

                Expect.isTrue
                    ((viewFields after) = (Case.snapshot original |> viewFields))
                    "Only the two explicit status transitions"

                Expect.equal
                    after.Revision
                    (CaseFixtures.revision (Revision.value (Case.snapshot original).Revision + 2L))
                    "Technical revisions")
            testCase "stale version cannot overwrite a case" (fun () ->
                let result =
                    CaseFixtures.decide
                        today
                        (request 0L (AuthoredCaseAct.Bare BareAct.Close))
                        (Some(opened ()))

                Expect.isTrue
                    ((result |> Result.map Case.snapshot) =
                        Error(Refusal.RevisionConflict(CaseFixtures.revision 1L)))
                    "No lost update")
            testCase "missing case rejected" (fun () ->
                let result =
                    CaseFixtures.decide
                        today
                        (request 1L (AuthoredCaseAct.Bare BareAct.Close))
                        None
                    |> Result.map Case.snapshot

                Expect.isTrue
                    (result = Error(Refusal.Access AccessRefusal.ResourceUnavailable))
                    "Not found")
        ]

let private paymentGuardTests =
    testList
        "payment guards"
        [
            testCase "payment requires decision" (fun () ->
                let result =
                    opened ()
                    |> apply (
                        AuthoredCaseAct.RecordPayment(
                            ClaimCore.TestSupport.Inputs.paymentFrom
                                [ BusinessField.PaymentDate, ("2026-08-20") ]
                        )
                    )
                    |> Result.map Case.snapshot

                Expect.isTrue
                    (result = Error(Refusal.Case CaseRefusal.DecisionRequired))
                    "Decision required")
            testCase "second full payment rejected" (fun () ->
                let result =
                    paid ()
                    |> apply (
                        AuthoredCaseAct.RecordPayment(
                            ClaimCore.TestSupport.Inputs.paymentFrom
                                [ BusinessField.PaymentDate, ("2026-08-21") ]
                        )
                    )
                    |> Result.map Case.snapshot

                Expect.isTrue
                    (result = Error(Refusal.Case CaseRefusal.PaymentAlreadyRecorded))
                    "No instalment model")
            testCase "zero decision allowed but no fictitious zero transfer" (fun () ->
                let claim =
                    opened ()
                    |> apply (
                        AuthoredCaseAct.Decide(
                            ClaimCore.TestSupport.Inputs.decisionWith
                                [ BusinessField.PayableAmount, ("0") ]
                                (decision)
                        )
                    )
                    |> accepted

                let result =
                    apply
                        (AuthoredCaseAct.RecordPayment(
                            ClaimCore.TestSupport.Inputs.paymentFrom
                                [ BusinessField.PaymentDate, ("2026-08-20") ]
                        ))
                        claim
                    |> Result.map Case.snapshot

                Expect.isTrue
                    (result = Error(Refusal.Case CaseRefusal.ZeroDecisionCannotBePaid))
                    "No transfer"

                Expect.isFalse
                    (availableActs claim |> List.contains "RECORD_PAYMENT")
                    "Advisory actions agree")
            testCase "payable can exceed claim and have a different currency" (fun () ->
                let input =
                    (ClaimCore.TestSupport.Inputs.decisionWith
                        [
                            BusinessField.PayableAmount, ("2500.01")
                            BusinessField.PayableCurrency, ("USD")
                        ]
                        (decision))

                let fields =
                    (opened ()
                     |> apply (AuthoredCaseAct.Decide input)
                     |> accepted
                     |> Case.snapshot
                     |> viewFields)

                Expect.isTrue
                    ((fields.Decision
                      |> Option.map (fun value -> Money.format (Decision.payable (value))))
                        =
                        Some "2500.01")
                    "No invented coverage cap"

                Expect.isTrue
                    ((fields.Decision
                      |> Option.map (fun value -> Money.currency (Decision.payable (value))))
                        =
                        Some "USD")
                    "No invented currency conversion")
        ]

let private decisionEditingTests =
    testList
        "decision editing"
        [
            testCase "paid decision cannot be edited" (fun () ->
                let result =
                    paid () |> apply (AuthoredCaseAct.Decide decision) |> Result.map Case.snapshot

                Expect.isTrue
                    (result = Error(Refusal.Case CaseRefusal.DecisionAlreadyPaid))
                    "Explicit correction first")
            testCase "unpaid decision can be replaced" (fun () ->
                let fields =
                    (decided ()
                     |> apply (
                         AuthoredCaseAct.Decide(
                             ClaimCore.TestSupport.Inputs.decisionWith
                                 [ BusinessField.PayableAmount, ("500") ]
                                 (decision)
                         )
                     )
                     |> accepted
                     |> Case.snapshot
                     |> viewFields)

                Expect.isTrue
                    ((fields.Decision
                      |> Option.map (fun value -> Money.format (Decision.payable (value))))
                        =
                        Some "500")
                    "Explicit replacement")
            testCase "registration amendment requires withdrawal of an existing decision" (fun () ->
                let amendment =
                    decided ()
                    |> apply (AuthoredCaseAct.AmendRegistration registration)
                    |> Result.map Case.snapshot

                Expect.isTrue
                    (amendment = Error(Refusal.Case CaseRefusal.AmendmentRequiresUndecided))
                    "No stale decision"

                let fields =
                    (decided ()
                     |> apply (AuthoredCaseAct.Bare BareAct.WithdrawDecision)
                     |> accepted
                     |> apply (AuthoredCaseAct.AmendRegistration registration)
                     |> accepted
                     |> Case.snapshot
                     |> viewFields)

                Expect.isNone
                    (fields.Decision
                     |> Option.map (fun value -> CalendarDate.format (Decision.date (value))))
                    "Decision withdrawn")
        ]

let private correctionTests =
    testList
        "correction state"
        [
            testCase "clear payment requires no extra claims-entry data" (fun () ->
                let before = paid ()

                let after =
                    before
                    |> apply (AuthoredCaseAct.Bare BareAct.ClearPayment)
                    |> accepted
                    |> Case.snapshot

                let expected =
                    { (Case.snapshot before |> viewFields) with
                        Progress =
                            PaymentProgress.Decided(
                                Option.get (Case.snapshot before |> viewFields).Decision
                            )
                    }

                Expect.isTrue ((viewFields after) = expected) "Only requested payment date cleared"

                Expect.equal
                    after.Revision
                    (CaseFixtures.revision 4L)
                    "History has a new technical revision")
            testCase "clear payment without a recorded date is rejected" (fun () ->
                let result =
                    decided ()
                    |> apply (AuthoredCaseAct.Bare BareAct.ClearPayment)
                    |> Result.map Case.snapshot

                Expect.isTrue
                    (result = Error(Refusal.Case CaseRefusal.PaymentNotRecorded))
                    "No payment")
            testCase "closed cases must reopen before edits" (fun () ->
                let claim = decided () |> apply (AuthoredCaseAct.Bare BareAct.Close) |> accepted

                let result =
                    apply
                        (AuthoredCaseAct.RecordPayment(
                            ClaimCore.TestSupport.Inputs.paymentFrom
                                [ BusinessField.PaymentDate, ("2026-08-20") ]
                        ))
                        claim
                    |> Result.map Case.snapshot

                Expect.isTrue
                    (result = Error(Refusal.Case CaseRefusal.CaseClosed))
                    "Explicit reopen")
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
