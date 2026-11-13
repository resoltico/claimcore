module ClaimCore.Tests.DomainAdmissionTests

open ClaimCore.TestSupport

open ClaimCore.Wire
open ClaimCore.Presentation

open Expecto
open ClaimCore.Domain
open ClaimCore.Tests.Fixtures

let private chronology field =
    RecordViolation.InvalidScalar(field, Violation.ChronologicalOrder)

let private snapshots =
    testCase
        "financial constructors and restoration reject invalid paid and chronological states"
        (fun () ->
            let original = paid () |> Case.snapshot
            let current = viewFields original
            let financial = current.Finance
            let decision = Option.get current.Decision

            let zero =
                Inputs.decisionStateWith
                    [
                        BusinessField.PayableAmount,
                        Admitted.Amount(Money.amount (CaseFixtures.money "0" "EUR"))
                    ]
                    decision

            Expect.equal
                (FinancialPosition.create
                    (Some zero)
                    financial.PaidAmount
                    financial.PaymentCount
                    financial.PaymentDates)
                (Error FinancialPositionIssue.ExceedsPayable)
                "Overpaid accepted state cannot be constructed"

            let earlier = CaseFixtures.date "2026-08-14"

            Expect.equal
                (FinancialPosition.create
                    (Some decision)
                    financial.PaidAmount
                    1L
                    (PaymentDateBounds.create earlier earlier))
                (Error FinancialPositionIssue.PaymentBeforeDecision)
                "Payment-before-decision state cannot be constructed"

            let beforeNotification =
                Inputs.decisionStateWith
                    [
                        BusinessField.PaymentDecisionDate,
                        Admitted.Date(CaseFixtures.date "2026-08-02")
                    ]
                    decision

            let crossGroup =
                FinancialPosition.create
                    (Some beforeNotification)
                    financial.PaidAmount
                    financial.PaymentCount
                    financial.PaymentDates
                |> accepted

            Expect.equal
                (Case.ofAccepted
                    { original with
                        State = CaseState.Active { current with Finance = crossGroup }
                    }
                 |> Result.map ignore)
                (Error(chronology (InputTarget.Field BusinessField.PaymentDecisionDate)))
                "Restoration checks registration against the financial position")

let private payload =
    testCase "malformed payload admission precedes stale revision and eligibility" (fun () ->
        let malformed =
            AuthoredCaseAct.Decide(
                ClaimCore.TestSupport.Inputs.decisionWith
                    [ BusinessField.PaymentDecisionDate, ("not-a-date") ]
                    (decision)
            )

        let rejected =
            CaseFixtures.decide today Map.empty (request 0L malformed) (Some(paid ()))
            |> Result.map ignore

        Expect.equal
            rejected
            (Error(
                CaseDecisionError.Refused(
                    Refusal.invalidInput (
                        (InputTarget.Field BusinessField.PaymentDecisionDate),
                        Violation.CalendarDateRequired
                    )
                )
            ))
            "Same admission ordering for parsed acts")

[<Tests>]
let tests = testList "domain accepted-state admission" [ snapshots; payload ]
