module ClaimCore.Tests.DomainAdmissionTests

open Expecto
open ClaimCore.Domain
open ClaimCore.Tests.Fixtures

let private chronology field =
    CaseRefusal.InvalidInput(field, InputViolation.Date DateViolation.ChronologicalOrder)

let private snapshots =
    testCase "[CC-DOM-001] restoration refuses invalid paid and chronological states" (fun () ->
        let original = paid () |> Case.snapshot

        let variants =
            [
                { (viewFields original) with
                    PayableAmount = Some "0"
                },
                CaseRefusal.ZeroDecisionCannotBePaid
                { (viewFields original) with
                    PaymentDecisionDate = Some "2026-08-02"
                },
                chronology InputTarget.PaymentDecisionDate
                { (viewFields original) with
                    PaymentDate = Some "2026-08-14"
                },
                chronology InputTarget.PaymentDate
            ]

        for fields, refusal in variants do
            Expect.equal
                (Case.ofAccepted (snapshot original.Revision fields) |> Result.map ignore)
                (Error refusal)
                "No invalid opaque Claim")

let private payload =
    testCase
        "[CC-DOM-002] malformed payload admission precedes stale revision and eligibility"
        (fun () ->
            let malformed =
                BusinessCommand.Decide
                    { decision with
                        PaymentDecisionDate = "not-a-date"
                    }

            let rejected =
                Case.decide today (request 0L malformed) (Some(paid ())) |> Result.map ignore

            Expect.equal
                rejected
                (Error(
                    CaseRefusal.InvalidInput(
                        InputTarget.PaymentDecisionDate,
                        InputViolation.Date DateViolation.CalendarDateRequired
                    )
                ))
                "Same admission ordering for parsed commands")

[<Tests>]
let tests = testList "domain accepted-state admission" [ snapshots; payload ]
