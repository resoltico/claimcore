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
        let payment = (Option.get current.Payment).PaymentDate

        let variants =
            [
                { current with
                    Progress =
                        PaymentProgress.Paid(
                            { decision with
                                PayableAmount = CaseFixtures.money "0" "EUR"
                            },
                            payment
                        )
                },
                RecordViolation.InvalidProgress
                { current with
                    Progress =
                        PaymentProgress.Paid(
                            { decision with
                                PaymentDecisionDate = CaseFixtures.date "2026-08-02"
                            },
                            payment
                        )
                },
                chronology (InputTarget.Field BusinessField.PaymentDecisionDate)
                { current with
                    Progress = PaymentProgress.Paid(decision, CaseFixtures.date "2026-08-14")
                },
                chronology (InputTarget.Field BusinessField.PaymentDate)
            ]

        for fields, refusal in variants do
            Expect.equal
                (Case.ofAccepted (snapshot original.Revision fields) |> Result.map ignore)
                (Error refusal)
                "No invalid opaque Claim")

let private payload =
    testCase "malformed payload admission precedes stale revision and eligibility" (fun () ->
        let malformed =
            AuthoredCaseAct.Decide
                { decision with
                    PaymentDecisionDate = "not-a-date"
                }

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
