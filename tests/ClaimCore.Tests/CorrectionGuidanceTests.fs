module ClaimCore.Tests.CorrectionGuidanceTests

open Expecto
open ClaimCore.Application
open ClaimCore.Contracts
open ClaimCore.Domain
open ClaimCore.Tests.Fixtures

let private changedRegistration =
    { registration with
        ClaimantName = "Corrected synthetic claimant"
    }

let private correction decision payment =
    Command.CorrectCase
        {
            Registration = RegistrationCorrection.Replace changedRegistration
            Decision = decision
            Payment = payment
        }

let private assertGuidance error ordinary before replacement =
    Expect.equal (before |> apply ordinary) (Error error) "Ordinary command refuses"
    let rejection = Rejection.Domain error
    Expect.equal rejection.Action RecommendedAction.NoneRequired "Delivery guidance unchanged"
    let message = RejectionPresentation.render rejection

    Expect.stringContains
        message
        (CommandDefinitions.forKind CommandKind.CorrectCase).Label
        "Supported alternative is named by its owner"

    let after = before |> apply replacement |> accepted |> Claim.view
    Expect.equal after.Fields.ClaimantName changedRegistration.ClaimantName "Correction accepted"
    Expect.equal after.Fields.Status (Claim.view before).Fields.Status "Status preserved"
    after

let private paidDecisionCorrection () =
    let before = paid ()

    let revised =
        { decision with
            PayableAmount = "650.00"
        }

    let after =
        assertGuidance
            DomainError.DecisionAlreadyPaid
            (Command.Decide revised)
            before
            (correction
                (DecisionCorrection.Replace revised)
                (PaymentCorrection.Replace "2026-08-20"))

    Expect.equal after.Fields.PayableAmount (Some "650") "Decision corrected"

    Expect.equal after.Fields.PaymentDate (Some "2026-08-20") "Payment explicitly reaffirmed"

let tests =
    testList
        "scoped factual correction guidance"
        [
            testCase
                "[CC-DOM-002] amendment refusal permits correction keeping the decision"
                (fun () ->
                    let before = decided ()

                    let after =
                        assertGuidance
                            DomainError.AmendmentRequiresUndecided
                            (Command.AmendRegistration changedRegistration)
                            before
                            (correction DecisionCorrection.Keep PaymentCorrection.Keep)

                    Expect.equal
                        after.Fields.PayableAmount
                        (Claim.view before).Fields.PayableAmount
                        "Decision kept")
            testCase
                "[CC-DOM-002] paid decision refusal permits correction with explicit payment reaffirmation"
                paidDecisionCorrection
            testCase
                "[CC-DOM-002] closed case refusal permits correction without reopening"
                (fun () ->
                    let before = paid () |> apply Command.Close |> accepted

                    let after =
                        assertGuidance
                            DomainError.ClosedCase
                            (Command.AmendRegistration changedRegistration)
                            before
                            (correction DecisionCorrection.Keep PaymentCorrection.Keep)

                    Expect.equal after.Fields.Status CaseStatus.Closed "Closed status retained")
        ]
