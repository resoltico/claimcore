module ClaimCore.Tests.CaseCorrectionTests

open ClaimCore.TestSupport
open Expecto
open ClaimCore.Domain
open ClaimCore.Tests.Fixtures

let private correctAllValues registration decision =
    AuthoredCaseAct.Correct
        {
            Registration = Inputs.registrationChoice registration
            Decision = Inputs.decisionChoice decision
        }

let private changedRegistration =
    Inputs.registrationWith
        [ BusinessField.ClaimantName, "Corrected Synthetic Claimant" ]
        registration

[<Tests>]
let tests =
    testList
        "atomic case correction"
        [
            testCase
                "paid corrections change claim and decision facts in one revision while retaining every payment"
                (fun () ->
                    let before = paid ()

                    let act =
                        correctAllValues
                            (Amendment.Update changedRegistration)
                            (Change.Update(
                                Inputs.decisionWith
                                    [ BusinessField.PayableAmount, "1500" ]
                                    decision
                            ))

                    let after = before |> apply act |> accepted |> Case.snapshot
                    Expect.equal after.Revision (CaseFixtures.revision 4L) "One revision"
                    let fields = viewFields after
                    Expect.equal fields.Status CaseStatus.Opened "Status is retained"

                    Expect.equal
                        (CaseReference.format fields.CaseReference)
                        "UNIT-001"
                        "Target is retained"

                    Expect.equal
                        (PartyName.format (Registration.claimant fields.Registration))
                        "Corrected Synthetic Claimant"
                        "Claim facts change"

                    Expect.equal
                        (fields.Decision |> Option.map (Decision.payable >> Money.format))
                        (Some "1500")
                        "Payable changes"

                    Expect.equal
                        (fields.Finance.PaidAmount,
                         fields.Finance.PaymentCount,
                         fields.Finance.PaymentDates)
                        (let prior = (viewFields (Case.snapshot before)).Finance in
                         prior.PaidAmount, prior.PaymentCount, prior.PaymentDates)
                        "Paid aggregate and date boundaries stay unchanged"

                    Expect.equal
                        (CaseProgress.ofFinancial fields.Finance)
                        CaseProgress.PartlyPaid
                        "A higher payable can leave an outstanding balance")
            testCase
                "paid decision corrections need no acknowledgement and cannot reduce payable below paid"
                (fun () ->
                    let before = paid ()

                    Expect.isError
                        (before
                         |> apply (
                             correctAllValues
                                 Amendment.Keep
                                 (Change.Update(
                                     Inputs.decisionWith
                                         [ BusinessField.PayableAmount, "650" ]
                                         decision
                                 ))
                         ))
                        "Recorded payments bound payable"

                    Expect.isOk
                        (before
                         |> apply (
                             correctAllValues
                                 Amendment.Keep
                                 (Change.Update(
                                     Inputs.decisionWith
                                         [ BusinessField.PayableAmount, "800" ]
                                         decision
                                 ))
                         ))
                        "No payment correction group is required")
            testCase "a corrected decision cannot be cleared while entries remain" (fun () ->
                Expect.isError
                    (paid () |> apply (correctAllValues Amendment.Keep Change.Clear))
                    "Entries cannot lose their decision")
            testCase
                "closed cases may correct facts while retaining closed status and payments"
                (fun () ->
                    let before = paid () |> apply (AuthoredCaseAct.Bare BareAct.Close) |> accepted

                    let after =
                        before
                        |> apply (
                            correctAllValues (Amendment.Update changedRegistration) Change.Keep
                        )
                        |> accepted
                        |> Case.snapshot

                    Expect.equal
                        (viewFields after).Status
                        CaseStatus.Closed
                        "Correction does not reopen"

                    Expect.equal
                        (PartyName.format (Registration.claimant (viewFields after).Registration))
                        "Corrected Synthetic Claimant"
                        "Claim fact changes"

                    Expect.equal
                        (viewFields after).Finance
                        (viewFields (Case.snapshot before)).Finance
                        "Financial position remains")
            testCase "all keep is refused without advancing the case" (fun () ->
                let before = paid ()

                Expect.equal
                    (before |> apply (correctAllValues Amendment.Keep Change.Keep))
                    (Error(CaseDecisionError.Refused(Refusal.Case CaseRefusal.CorrectionNoChanges)))
                    "No-op is refused"

                Expect.equal
                    (Case.snapshot before).Revision
                    (CaseFixtures.revision 3L)
                    "Original revision remains")
            testCase "equivalent scalar spelling cannot create a correction revision" (fun () ->
                Expect.equal
                    (paid () |> apply (correctAllValues Amendment.Keep (Change.Update decision)))
                    (Error(CaseDecisionError.Refused(Refusal.Case CaseRefusal.CorrectionNoChanges)))
                    "Equal decision facts are not a change")
            testCase "correction cannot add a decision to an undecided open case" (fun () ->
                let act =
                    correctAllValues
                        (Amendment.Update changedRegistration)
                        (Change.Update decision)

                Expect.equal
                    (opened () |> apply act)
                    (Error(
                        CaseDecisionError.Refused(
                            Refusal.Case(CaseRefusal.NotOffered CaseHeadline.Undecided)
                        )
                    ))
                    "Existing decision facts are required")
            testCase "newly asserted correction dates cannot be future dates" (fun () ->
                let changed =
                    Inputs.registrationWith
                        [
                            BusinessField.IncidentDate, "2026-09-08"
                            BusinessField.IncidentNotificationDate, "2026-09-08"
                        ]
                        changedRegistration

                Expect.isTrue
                    (paid ()
                     |> apply (correctAllValues (Amendment.Update changed) Change.Keep)
                     |> isInvalid)
                    "New assertions use the business date")
        ]
