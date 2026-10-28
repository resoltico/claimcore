module ClaimCore.Tests.CaseCorrectionTests

open ClaimCore.TestSupport

open ClaimCore.Protocol

open Expecto
open ClaimCore.Domain
open ClaimCore.Tests.Fixtures

let private correct registration decision payment =
    AuthoredCaseAct.Correct
        {
            Registration = registration
            Decision = decision
            Payment = payment
        }

let private changedRegistration =
    { registration with
        ClaimantName = "Corrected Synthetic Claimant"
    }

let private paidCorrectionTests =
    testList
        "paid correction"
        [
            testCase "paid corrections replace related facts in one complete revision" (fun () ->
                let before = paid ()

                let act =
                    correct
                        (Amendment.Replace changedRegistration)
                        (Change.Replace
                            { decision with
                                PayableAmount = "650.00"
                            })
                        (Change.Replace { PaymentDate = "2026-08-21" })

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
                    (PartyName.format (viewFields after).Registration.ClaimantName)
                    changedRegistration.ClaimantName
                    "Registration replaced"

                Expect.equal
                    ((viewFields after).Decision
                     |> Option.map (fun value -> Money.format value.PayableAmount))
                    (Some "650")
                    "Decision replaced"

                Expect.equal
                    ((viewFields after).Payment
                     |> Option.map (fun value -> CalendarDate.format value.PaymentDate))
                    (Some "2026-08-21")
                    "Payment reaffirmed")
            testCase "paid decision replacement requires explicit payment action" (fun () ->
                let before = paid ()

                let act =
                    correct
                        Amendment.Keep
                        (Change.Replace { decision with PayableAmount = "650" })
                        Change.Keep

                Expect.isError (before |> apply act) "Paid decision is not silently reused")
            testCase "clearing a paid decision requires clearing its payment record" (fun () ->
                let before = paid ()

                let act = correct Amendment.Keep Change.Clear Change.Keep

                Expect.isError (before |> apply act) "Payment cannot outlive a cleared decision")
        ]

let private closedCorrectionTests =
    testList
        "closed correction"
        [
            testCase "closed cases may correct facts while retaining CLOSED status" (fun () ->
                let closed = paid () |> apply AuthoredCaseAct.Close |> accepted

                let after =
                    closed
                    |> apply (
                        correct (Amendment.Replace changedRegistration) Change.Keep Change.Keep
                    )
                    |> accepted
                    |> Case.snapshot

                Expect.equal
                    (viewFields after).Status
                    CaseStatus.Closed
                    "Correction does not reopen"

                Expect.equal
                    (PartyName.format (viewFields after).Registration.ClaimantName)
                    changedRegistration.ClaimantName
                    "Fact corrected")
            testCase "all-keep correction is refused without changing the accepted case" (fun () ->
                let before = paid ()

                let result = before |> apply (correct Amendment.Keep Change.Keep Change.Keep)

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
                            correct
                                Amendment.Keep
                                (Change.Replace decision)
                                (Change.Replace { PaymentDate = "2026-08-20" })
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
                    correct
                        (Amendment.Replace changedRegistration)
                        (Change.Replace decision)
                        Change.Keep

                Expect.equal
                    (opened () |> apply act)
                    (Error(Refusal.Case CaseRefusal.CorrectionRequiresExistingValue))
                    "Correction works on existing decided, paid, or closed facts")
            testCase "newly asserted correction dates cannot be future dates" (fun () ->
                let act =
                    correct
                        (Amendment.Replace
                            { changedRegistration with
                                IncidentDate = "2026-09-08"
                                IncidentNotificationDate = "2026-09-08"
                            })
                        Change.Keep
                        Change.Keep

                Expect.isTrue
                    (paid () |> apply act |> isInvalid)
                    "New correction dates use business time")
        ]

[<Tests>]
let tests =
    testList
        "atomic case correction"
        [ paidCorrectionTests; closedCorrectionTests; validationTests ]
