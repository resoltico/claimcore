module ClaimCore.Tests.CaseChangesTests

open System
open Expecto
open ClaimCore.Application
open ClaimCore.Application.Storage
open ClaimCore.Application.Reads
open ClaimCore.Application.Execution
open ClaimCore.Application.Verification
open ClaimCore.Application.Contracts
open ClaimCore.Domain
open ClaimCore.Domain.Changes
open ClaimCore.TestSupport

let private identity = Guid.Parse "90000000-0000-7000-8000-000000000001"

let private fields: CaseFields =
    {
        CaseReference = CaseFixtures.reference "CORE-HISTORY"
        Status = CaseStatus.Opened
        Registration = Inputs.admittedRegistration Inputs.registration
        Finance = FinancialPosition.undecided
    }

let private live name =
    let registration =
        Registration.values fields.Registration
        |> List.map (fun (key, value) ->
            key,
            if key = BusinessField.ClaimantName then
                Admitted.Name(CaseFixtures.name name)
            else
                value)
        |> Registration.create
        |> Result.defaultWith failtest

    {
        CaseValue.Reference = None
        Lifecycle = Lifecycle.Active
        Status = CaseStatus.Opened
        Content =
            Some
                {
                    Registration = registration
                    Finance = fields.Finance
                }
        Disposition = None
    }

let private lifecycle before after =
    (CaseDescription.describe fields.CaseReference before after).Lifecycle
    |> Option.map (fun change ->
        {
            LifecycleChange.Before = change.Before
            After = change.After |> Option.get
        })

[<Tests>]
let tests =
    testCase "timeline lifecycle differences preserve creation, changes and stable state" (fun () ->
        let active = live "Before"

        let voided =
            { live "After" with
                Lifecycle = Lifecycle.Voided
            }

        Expect.equal
            (lifecycle None active)
            (Some
                {
                    Before = None
                    After = Lifecycle.Active
                })
            "Creation has no predecessor lifecycle"

        Expect.equal
            (lifecycle (Some active) (live "Changed name"))
            None
            "Business changes do not fabricate a lifecycle change"

        Expect.equal
            (lifecycle (Some active) voided)
            (Some
                {
                    Before = Some Lifecycle.Active
                    After = Lifecycle.Voided
                })
            "Voiding retains both lifecycle values"

        Expect.equal
            (lifecycle (Some voided) active)
            (Some
                {
                    Before = Some Lifecycle.Voided
                    After = Lifecycle.Active
                })
            "Reinstatement retains both lifecycle values")
