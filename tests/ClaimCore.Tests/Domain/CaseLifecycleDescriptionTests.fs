module ClaimCore.Tests.CaseLifecycleDescriptionTests

open Expecto
open ClaimCore.Domain
open ClaimCore.Domain.Changes
open ClaimCore.TestSupport

let private fields: CaseFields =
    {
        CaseReference = CaseFixtures.reference "CC-8JBJ-CAB4-7BQ1-WBX7-44"
        Handling = Handling.Open
        Registration = DomainInputs.admittedRegistration DomainInputs.registration
        Finance = FinancialPosition.undecided
    }

let private live name =
    let registration =
        Registration.values fields.Registration
        |> List.map (fun (key, value) ->
            key,
            if key = BusinessField.PolicyNumber then
                Admitted.Identifier(
                    Identifier.parse name |> Result.defaultWith (string >> failwith)
                )
            else
                value)
        |> Registration.create
        |> Result.defaultWith failtest

    {
        CaseValue.Reference = None
        Lifecycle = Lifecycle.Active
        Handling = Handling.Open
        Content =
            Some
                {
                    Registration = registration
                    Finance = fields.Finance
                }
        Disposition = None
        ReopenReason = None
    }

let private lifecycle before after =
    (CaseDescription.describe fields.CaseReference before after).Lifecycle

[<Tests>]
let tests =
    testCase "case lifecycle descriptions preserve voiding and reinstatement" (fun () ->
        let active = live "Before"

        let voided =
            { live "After" with
                Lifecycle = Lifecycle.Voided
            }

        Expect.equal
            (lifecycle (Some active) voided)
            (Some
                {
                    Before = Some Lifecycle.Active
                    After = Some Lifecycle.Voided
                })
            "Voiding retains both lifecycle values"

        Expect.equal
            (lifecycle (Some voided) active)
            (Some
                {
                    Before = Some Lifecycle.Voided
                    After = Some Lifecycle.Active
                })
            "Reinstatement retains both lifecycle values")
