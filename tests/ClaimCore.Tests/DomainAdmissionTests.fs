module ClaimCore.Tests.DomainAdmissionTests

open Expecto
open ClaimCore.Domain
open ClaimCore.Tests.Fixtures

let private chronology field =
    DomainError.InvalidInput(field, InputViolation.Date DateViolation.ChronologicalOrder)

let private snapshots =
    testCase "[CC-DOM-001] restoration refuses invalid paid and chronological states" (fun () ->
        let original = paid () |> Claim.view

        let variants =
            [
                { original.Fields with
                    PayableAmount = Some "0"
                },
                DomainError.ZeroDecisionCannotBePaid
                { original.Fields with
                    PaymentDecisionDate = Some "2026-08-02"
                },
                chronology InputTarget.PaymentDecisionDate
                { original.Fields with
                    PaymentDate = Some "2026-08-14"
                },
                chronology InputTarget.PaymentDate
            ]

        for fields, refusal in variants do
            Expect.equal
                (Claim.restore { original with Fields = fields } |> Result.map ignore)
                (Error refusal)
                "No invalid opaque Claim")

let private payload =
    testCase
        "[CC-DOM-002] malformed payload admission precedes stale revision and eligibility"
        (fun () ->
            let malformed =
                Command.Decide
                    { decision with
                        PaymentDecisionDate = "not-a-date"
                    }

            let rejected =
                Claim.decide today (request 0L malformed) (Some(paid ())) |> Result.map ignore

            Expect.equal
                rejected
                (Error(
                    DomainError.InvalidInput(
                        InputTarget.PaymentDecisionDate,
                        InputViolation.Date DateViolation.CalendarDateRequired
                    )
                ))
                "Same admission ordering for parsed commands")

let tests = testList "domain accepted-state admission" [ snapshots; payload ]
