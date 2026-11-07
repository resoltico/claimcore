module ClaimCore.Tests.CaseChangesTests

open System
open Expecto
open ClaimCore.Application
open ClaimCore.Application.Contracts
open ClaimCore.Domain
open ClaimCore.TestSupport

let private identity = Guid.Parse "90000000-0000-7000-8000-000000000001"

let private fields: CaseFields =
    {
        CaseReference = CaseFixtures.reference "CORE-HISTORY"
        Status = CaseStatus.Opened
        Registration = Inputs.admittedRegistration Inputs.registration
        Progress = PaymentProgress.Undecided
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

    LiveCase.Active
        { fields with
            Registration = registration
        }

[<Tests>]
let tests =
    testCase "timeline lifecycle differences preserve creation, changes and stable state" (fun () ->
        let active = live "Before"

        let voided =
            match live "After" with
            | LiveCase.Active fields -> LiveCase.Voided fields
            | _ -> failtest "Active fixture"

        Expect.equal
            (CaseChanges.lifecycle None active)
            (Some
                {
                    Before = None
                    After = Lifecycle.Active
                })
            "Creation has no predecessor lifecycle"

        Expect.equal
            (CaseChanges.lifecycle (Some active) (live "Changed name"))
            None
            "Business changes do not fabricate a lifecycle change"

        Expect.equal
            (CaseChanges.lifecycle (Some active) voided)
            (Some
                {
                    Before = Some Lifecycle.Active
                    After = Lifecycle.Voided
                })
            "Voiding retains both lifecycle values"

        Expect.equal
            (CaseChanges.lifecycle (Some voided) active)
            (Some
                {
                    Before = Some Lifecycle.Voided
                    After = Lifecycle.Active
                })
            "Reinstatement retains both lifecycle values")
