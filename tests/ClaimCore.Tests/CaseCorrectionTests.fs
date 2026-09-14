module ClaimCore.Tests.CaseCorrectionTests

open Expecto
open ClaimCore.Domain
open ClaimCore.Tests.Fixtures

let private correct registration decision payment =
    Command.CorrectCase
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

                let command =
                    correct
                        (RegistrationCorrection.Replace changedRegistration)
                        (DecisionCorrection.Replace
                            { decision with
                                PayableAmount = "650.00"
                            })
                        (PaymentCorrection.Replace "2026-08-21")

                let after = before |> apply command |> accepted |> Claim.view

                Expect.equal after.Version ((Claim.view before).Version + 1L) "One revision"
                Expect.equal after.Fields.Status CaseStatus.Opened "Status is retained"
                Expect.equal after.Fields.CaseReference "UNIT-001" "Case target is retained"

                Expect.equal
                    after.Fields.ClaimantName
                    changedRegistration.ClaimantName
                    "Registration replaced"

                Expect.equal after.Fields.PayableAmount (Some "650") "Decision replaced"
                Expect.equal after.Fields.PaymentDate (Some "2026-08-21") "Payment reaffirmed")
            testCase "paid decision replacement requires explicit payment action" (fun () ->
                let before = paid ()

                let command =
                    correct
                        RegistrationCorrection.Keep
                        (DecisionCorrection.Replace { decision with PayableAmount = "650" })
                        PaymentCorrection.Keep

                Expect.isError (before |> apply command) "Paid decision is not silently reused")
            testCase "clearing a paid decision requires clearing its payment record" (fun () ->
                let before = paid ()

                let command =
                    correct
                        RegistrationCorrection.Keep
                        DecisionCorrection.Clear
                        PaymentCorrection.Keep

                Expect.isError
                    (before |> apply command)
                    "Payment cannot outlive a cleared decision")
        ]

let private closedCorrectionTests =
    testList
        "closed correction"
        [
            testCase "closed cases may correct facts while retaining CLOSED status" (fun () ->
                let closed = paid () |> apply Command.Close |> accepted

                let after =
                    closed
                    |> apply (
                        correct
                            (RegistrationCorrection.Replace changedRegistration)
                            DecisionCorrection.Keep
                            PaymentCorrection.Keep
                    )
                    |> accepted
                    |> Claim.view

                Expect.equal after.Fields.Status CaseStatus.Closed "Correction does not reopen"

                Expect.equal
                    after.Fields.ClaimantName
                    changedRegistration.ClaimantName
                    "Fact corrected")
            testCase "all-keep correction is refused without changing the accepted case" (fun () ->
                let before = paid ()

                let result =
                    before
                    |> apply (
                        correct
                            RegistrationCorrection.Keep
                            DecisionCorrection.Keep
                            PaymentCorrection.Keep
                    )

                Expect.equal result (Error DomainError.CorrectionNoChanges) "No-op is refused"
                Expect.equal (Claim.view before).Version 3L "Existing case is unchanged")
            testCase
                "equivalent scalar spelling cannot create a correction-only revision"
                (fun () ->
                    let before = paid ()

                    let result =
                        before
                        |> apply (
                            correct
                                RegistrationCorrection.Keep
                                (DecisionCorrection.Replace decision)
                                (PaymentCorrection.Replace "2026-08-20")
                        )

                    Expect.equal
                        result
                        (Error DomainError.CorrectionNoChanges)
                        "Same accepted facts are no-op")
        ]

let private validationTests =
    testList
        "correction validation"
        [
            testCase "correction cannot add a decision to an undecided case" (fun () ->
                let command =
                    correct
                        (RegistrationCorrection.Replace changedRegistration)
                        (DecisionCorrection.Replace decision)
                        PaymentCorrection.Keep

                Expect.equal
                    (opened () |> apply command)
                    (Error DomainError.CorrectionRequiresExistingValue)
                    "Correction works on existing decided, paid, or closed facts")
            testCase "newly asserted correction dates cannot be future dates" (fun () ->
                let command =
                    correct
                        (RegistrationCorrection.Replace
                            { changedRegistration with
                                IncidentDate = "2026-09-08"
                                IncidentNotificationDate = "2026-09-08"
                            })
                        DecisionCorrection.Keep
                        PaymentCorrection.Keep

                Expect.isTrue
                    (paid () |> apply command |> isInvalid)
                    "New correction dates use business time")
        ]

let tests =
    testList
        "atomic case correction"
        [ paidCorrectionTests; closedCorrectionTests; validationTests ]
