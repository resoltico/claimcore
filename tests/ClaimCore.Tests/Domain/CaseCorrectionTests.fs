module ClaimCore.Tests.CaseCorrectionTests

open ClaimCore.TestSupport

open ClaimCore.Protocol

open Expecto
open ClaimCore.Domain
open ClaimCore.Tests.Fixtures

let private correctAllValues registration decision payment =
    AuthoredCaseAct.Correct
        {
            Registration = Inputs.registrationChoice registration
            Decision = Inputs.decisionChoice decision
            Payment = payment
        }

let private changedRegistration =
    (ClaimCore.TestSupport.Inputs.registrationWith
        [ BusinessField.ClaimantName, ("Corrected Synthetic Claimant") ]
        (registration))

let private paidCorrectionTests =
    testList
        "paid correction"
        [
            testCase "paid corrections replace related facts in one complete revision" (fun () ->
                let before = paid ()

                let act =
                    correctAllValues
                        (Amendment.Update changedRegistration)
                        (Change.Update(
                            ClaimCore.TestSupport.Inputs.decisionWith
                                [ BusinessField.PayableAmount, ("650.00") ]
                                (decision)
                        ))
                        (PaymentChange.Replace(
                            ClaimCore.TestSupport.Inputs.paymentFrom
                                [ BusinessField.PaymentDate, ("2026-08-21") ]
                        ))

                let after = before |> apply act |> accepted |> Case.snapshot

                Expect.equal
                    after.Revision
                    (CaseFixtures.revision (Revision.value (Case.snapshot before).Revision + 1L))
                    "One revision"

                Expect.equal (viewFields after).Status CaseStatus.Opened "Status is retained"

                Expect.equal
                    (CaseReference.format (viewFields after).CaseReference)
                    "UNIT-001"
                    "Case target is retained"

                Expect.equal
                    (PartyName.format (Registration.claimant ((viewFields after).Registration)))
                    (RegistrationInput.value BusinessField.ClaimantName (changedRegistration))
                    "Registration replaced"

                Expect.equal
                    ((viewFields after).Decision
                     |> Option.map (fun value -> Money.format (Decision.payable (value))))
                    (Some "650")
                    "Decision replaced"

                Expect.equal
                    ((viewFields after).Payment
                     |> Option.map (fun value -> CalendarDate.format (Payment.date (value))))
                    (Some "2026-08-21")
                    "Payment reaffirmed")
            testCase "paid decision replacement requires explicit payment action" (fun () ->
                let before = paid ()

                let act =
                    correctAllValues
                        Amendment.Keep
                        (Change.Update(
                            ClaimCore.TestSupport.Inputs.decisionWith
                                [ BusinessField.PayableAmount, ("650") ]
                                (decision)
                        ))
                        PaymentChange.Keep

                Expect.isError (before |> apply act) "Paid decision is not silently reused")
            testCase "clearing a paid decision requires clearing its payment record" (fun () ->
                let before = paid ()

                let act = correctAllValues Amendment.Keep Change.Clear PaymentChange.Keep

                Expect.isError (before |> apply act) "Payment cannot outlive a cleared decision")
        ]

let private closedCorrectionTests =
    testList
        "closed correction"
        [
            testCase "closed cases may correct facts while retaining CLOSED status" (fun () ->
                let closed = paid () |> apply (AuthoredCaseAct.Bare BareAct.Close) |> accepted

                let after =
                    closed
                    |> apply (
                        correctAllValues
                            (Amendment.Update changedRegistration)
                            Change.Keep
                            PaymentChange.Keep
                    )
                    |> accepted
                    |> Case.snapshot

                Expect.equal
                    (viewFields after).Status
                    CaseStatus.Closed
                    "Correction does not reopen"

                Expect.equal
                    (PartyName.format (Registration.claimant ((viewFields after).Registration)))
                    (RegistrationInput.value BusinessField.ClaimantName (changedRegistration))
                    "Fact corrected")
            testCase "all-keep correction is refused without changing the accepted case" (fun () ->
                let before = paid ()

                let result =
                    before
                    |> apply (correctAllValues Amendment.Keep Change.Keep PaymentChange.Keep)

                Expect.equal
                    result
                    (Error(Refusal.Case CaseRefusal.CorrectionNoChanges))
                    "No-op is refused"

                Expect.equal
                    (Case.snapshot before).Revision
                    (CaseFixtures.revision 3L)
                    "Existing case is unchanged")
            testCase
                "equivalent scalar spelling cannot create a correction-only revision"
                (fun () ->
                    let before = paid ()

                    let result =
                        before
                        |> apply (
                            correctAllValues
                                Amendment.Keep
                                (Change.Update decision)
                                (PaymentChange.Replace(
                                    ClaimCore.TestSupport.Inputs.paymentFrom
                                        [ BusinessField.PaymentDate, ("2026-08-20") ]
                                ))
                        )

                    Expect.equal
                        result
                        (Error(Refusal.Case CaseRefusal.CorrectionNoChanges))
                        "Same accepted facts are no-op")
        ]

let private validationTests =
    testList
        "correction validation"
        [
            testCase "correction cannot add a decision to an undecided case" (fun () ->
                let act =
                    correctAllValues
                        (Amendment.Update changedRegistration)
                        (Change.Update decision)
                        PaymentChange.Keep

                Expect.equal
                    (opened () |> apply act)
                    (Error(Refusal.Case CaseRefusal.CorrectionRequiresExistingValue))
                    "Correction works on existing decided, paid, or closed facts")
            testCase "newly asserted correction dates cannot be future dates" (fun () ->
                let act =
                    correctAllValues
                        (Amendment.Update(
                            ClaimCore.TestSupport.Inputs.registrationWith
                                [
                                    BusinessField.IncidentDate, ("2026-09-08")
                                    BusinessField.IncidentNotificationDate, ("2026-09-08")
                                ]
                                (changedRegistration)
                        ))
                        Change.Keep
                        PaymentChange.Keep

                Expect.isTrue
                    (paid () |> apply act |> isInvalid)
                    "New correction dates use business time")
        ]

[<Tests>]
let tests =
    testList
        "atomic case correction"
        [ paidCorrectionTests; closedCorrectionTests; validationTests ]
