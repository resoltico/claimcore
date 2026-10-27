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

        let variants =
            [
                { (viewFields original) with
                    Decision =
                        (viewFields original).Decision
                        |> Option.map (fun value ->
                            { value with
                                PayableAmount = CaseFixtures.money "0" "EUR"
                            })
                },
                RecordViolation.InvalidProgress
                { (viewFields original) with
                    Decision =
                        (viewFields original).Decision
                        |> Option.map (fun value ->
                            { value with
                                PaymentDecisionDate = CaseFixtures.date "2026-08-02"
                            })
                },
                chronology (InputTarget.Field BusinessField.PaymentDecisionDate)
                { (viewFields original) with
                    Payment =
                        Some
                            {
                                PaymentDate = CaseFixtures.date "2026-08-14"
                            }
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
            UnvalidatedCaseAct.Decide
                { decision with
                    PaymentDecisionDate = "not-a-date"
                }

        let rejected =
            CaseFixtures.decide today (request 0L malformed) (Some(paid ()))
            |> Result.map ignore

        Expect.equal
            rejected
            (Error(
                Refusal.InvalidInput(
                    (InputTarget.Field BusinessField.PaymentDecisionDate),
                    Violation.CalendarDateRequired
                )
            ))
            "Same admission ordering for parsed acts")

[<Tests>]
let tests = testList "domain accepted-state admission" [ snapshots; payload ]
