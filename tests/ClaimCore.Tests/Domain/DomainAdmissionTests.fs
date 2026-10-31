module ClaimCore.Tests.DomainAdmissionTests

open ClaimCore.TestSupport

open ClaimCore.Protocol

open Expecto
open ClaimCore.Domain
open ClaimCore.Tests.Fixtures

let private chronology field =
    RecordViolation.InvalidScalar(field, Violation.ChronologicalOrder)

let private snapshots =
    testCase "restoration refuses invalid paid and chronological states" (fun () ->
        let original = paid () |> Case.snapshot

        let current = viewFields original
        let decision = Option.get current.Decision
        let payment = Option.get current.Payment

        let variants =
            [
                { current with
                    Progress =
                        PaymentProgress.Paid(
                            (ClaimCore.TestSupport.Inputs.decisionStateWith
                                [
                                    BusinessField.PayableAmount,
                                    Admitted.Amount(Money.amount (CaseFixtures.money "0" "EUR"))
                                    BusinessField.PayableCurrency,
                                    Admitted.Currency(
                                        Money.currencyValue (CaseFixtures.money "0" "EUR")
                                    )
                                ]
                                (decision)),
                            payment
                        )
                },
                RecordViolation.InvalidProgress
                { current with
                    Progress =
                        PaymentProgress.Paid(
                            (ClaimCore.TestSupport.Inputs.decisionStateWith
                                [
                                    BusinessField.PaymentDecisionDate,
                                    Admitted.Date(CaseFixtures.date "2026-08-02")
                                ]
                                (decision)),
                            payment
                        )
                },
                chronology (InputTarget.Field BusinessField.PaymentDecisionDate)
                { current with
                    Progress =
                        PaymentProgress.Paid(
                            decision,
                            Payment.onDate (CaseFixtures.date "2026-08-14")
                        )
                },
                chronology (InputTarget.Field BusinessField.PaymentDate)
            ]

        for fields, refusal in variants do
            Expect.equal
                (Case.ofAccepted (snapshot (Revision.value original.Revision) fields)
                 |> Result.map ignore)
                (Error refusal)
                "No invalid opaque Claim")

let private payload =
    testCase "malformed payload admission precedes stale revision and eligibility" (fun () ->
        let malformed =
            AuthoredCaseAct.Decide(
                ClaimCore.TestSupport.Inputs.decisionWith
                    [ BusinessField.PaymentDecisionDate, ("not-a-date") ]
                    (decision)
            )

        let rejected =
            CaseFixtures.decide today (request 0L malformed) (Some(paid ()))
            |> Result.map ignore

        Expect.equal
            rejected
            (Error(
                Refusal.invalidInput (
                    (InputTarget.Field BusinessField.PaymentDecisionDate),
                    Violation.CalendarDateRequired
                )
            ))
            "Same admission ordering for parsed acts")

[<Tests>]
let tests = testList "domain accepted-state admission" [ snapshots; payload ]
